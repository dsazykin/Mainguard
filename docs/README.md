# Documentation — start here

Everything under `docs/` is planning, design, and analysis material. **The code is the current
state; some of these docs are the destination.** Where a doc and the code disagree about what
exists today, the code wins — see [`../AGENTS.md`](../AGENTS.md) *What Mainguard Is*.

Anything superseded lives in [`archive/`](archive/README.md) and is not current.

## New here? Read these four, in order

| # | Read | Why |
|---|---|---|
| 1 | [`../README.md`](../README.md) | What the product is, and what is built vs. planned. |
| 2 | [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | Build, test, run. The contributor on-ramp. |
| 3 | [`../AGENTS.md`](../AGENTS.md) | **The source of truth** for architecture, conventions, and the hard review rules. |
| 4 | [`repo-map/README.md`](repo-map/README.md) | Where any given file lives, indexed per project. Use it instead of re-scanning the tree. |

`../CLAUDE.md`, `../GEMINI.md` and `../CHATGPT.md` are thin per-assistant pointers at `AGENTS.md`;
they carry no rules of their own.

## Engineering

| Folder | Files | What is in it |
|---|---:|---|
| [`repo-map/`](repo-map/README.md) | 10 | Per-file index of the whole repo, split by project. **Update it in the same change whenever you add, move, rename, or delete a file.** |
| [`phase-2/`](phase-2/) | 12 | The active agent-platform corpus. `implementation_plans/Mainguard_Master_Implementation_Document_v2.md` is **the binding spec** — where a subordinate doc disagrees, the master doc wins. |
| [`design/`](design/README.md) | 22 | UI/UX design output and per-feature design records. `README.md` is the design hub — start there. |
| [`creative/`](creative/README.md) | 9 | Voice, brand, microcopy, motion. `README.md` is the brand hub; the Voice & Delight Bible is the law. Consult it before writing any user-facing string. |
| [`review/`](review/) | 9 | How to test and review: the phase-1/2/3 hands-on procedures, the agent-cycle runbook, the full test matrix, and the manual user-testing guide. |
| [`adr/`](adr/) | 1 | Architecture Decision Records. Cross-cutting decisions, `Status → Context → Decision → Consequences`. |
| [`planning/`](planning/) | 1 | `Mainguard_Backlog.md` — the single source of truth for what is **not** built yet. |
| [`security-architecture.md`](security-architecture.md) | 1 | The sandbox, egress, and merge-safety threat model. |
| [`mainguardos-updates.md`](mainguardos-updates.md) | 1 | The Mainguard OS payload update mechanism. |

## Business

Not needed to build or review code, but these three are the whole business and the place to start for
anything that isn't code. Consolidated 2026-09-16 from the research corpus in
[`business/`](business/), which each section links back into for depth.

**Read these two to understand Mainguard.** The other two are reference you open when doing a
specific job.

| Read | What is in it |
|---|---|
| [`BUSINESS.md`](BUSINESS.md) | **Strategy.** What we sell and refuse to sell, the product and its differentiation spine, market numbers, the competitive field, ICP and positioning, pricing and unit economics, the moat and its erosion dashboard, risks, metrics, open decisions. |
| [`GTM.md`](GTM.md) | **Execution.** The current stage rule, the two-act launch sequence, the founding-user and design-partner programs, channels, the sales motion, when to raise, and the gates-before-dates calendar. |

| Open when you need it | What is in it |
|---|---|
| [`GTM_Assets.md`](GTM_Assets.md) | **The copy, as templates.** One-pager, investor deck, demo script, outreach sequences, site and email copy, the scored target-company list, grant drafts, Show HN bodies, the twelve standing objections and their answers, press and video material. |
| [`business/NL_Operations.md`](business/NL_Operations.md) | **Dutch lookups.** Grant schemes and deadlines, the fund and angel landscape, incorporating and invoicing, hiring and salary bands, the press and events map. |

| Folder | Files | What is in it |
|---|---:|---|
| [`business/go-to-market/`](business/go-to-market/) | 26 | The research behind the docs above: the master market document, decks, marketing, sales, grants. **Read the summary first** — where the two disagree, the summary wins. |
| [`business/market-analysis/`](business/market-analysis/) | 7 | Competitor and market research, with the full source lists. |
| [`business/members/`](business/members/) | 3 | Team structure and intake. |

## Archive

[`archive/`](archive/README.md) — **nothing in it is current.** Superseded material kept in full so
the reasoning survives: the phase-1 planning corpus, the shipped phase-2 feature plans, the retired
git-core task plans, point-in-time reports and walkthroughs, and the completed rebrand plan. Read it
for provenance, never as a spec.

## Where does a new doc go?

Put it in the matching folder above. A loose new `docs/*.md` is drift — the five that exist
(`BUSINESS.md`, `GTM.md`, `GTM_Assets.md`, `security-architecture.md`, `mainguardos-updates.md`) are
deliberate top-level entry points, not a precedent. The business docs sit here on purpose: they are the
first thing a non-engineering reader should find, and there is exactly one of each. Dutch operational
lookups live one level down, in `business/NL_Operations.md`, because nobody needs them to understand the
product.

**When a doc is superseded or its work ships, move it to `archive/` and add a one-line status note
at the top** saying what replaced it and when. Don't delete it, and don't leave it in place looking
current — a shipped plan that still reads as a plan is how a new contributor ends up building
something twice.

Relative links between docs are checked: keep them resolving when you move a file.
