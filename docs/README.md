# Documentation — start here

Everything under `docs/` is planning, design, and analysis material. **The code is the current
state; some of these docs are the destination.** Where a doc and the code disagree about what
exists today, the code wins — see [`../AGENTS.md`](../AGENTS.md) *What Mainguard Is*.

Anything superseded lives in [`archive/`](archive/README.md) and is not current.

## New here? Read these five, in order

| # | Read | Why |
|---|---|---|
| 1 | [`../README.md`](../README.md) | What the product is, and what is built vs. planned. |
| 2 | [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | Build, test, run. The contributor on-ramp. |
| 3 | [`STATUS.md`](STATUS.md) | **Is X done?** One row per task, status derived from the code rather than from prose. Check here before starting anything. |
| 4 | [`../AGENTS.md`](../AGENTS.md) | **The source of truth** for architecture, conventions, and the hard review rules. |
| 5 | [`repo-map/README.md`](repo-map/README.md) | Where any given file lives, indexed per project. Use it instead of re-scanning the tree. |

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
| [`STATUS.md`](STATUS.md) | 1 | **What is built and what is not**, per task, with the file or type that proves each claim. The binding spec does not record status; this does. |
| [`security-architecture.md`](security-architecture.md) | 1 | The sandbox, egress, and merge-safety threat model. |
| [`mainguardos-updates.md`](mainguardos-updates.md) | 1 | The Mainguard OS payload update mechanism. |

## Business

Not needed to build or review code. Grouped so `docs/` opens with engineering material.

| Folder | Files | What is in it |
|---|---:|---|
| [`business/go-to-market/`](business/go-to-market/) | 26 | Market & GTM strategy, decks, marketing, sales. `GTM_Execution.md` is the execution hub. |
| [`business/market-analysis/`](business/market-analysis/) | 7 | Competitor and market research. |
| [`business/members/`](business/members/) | 3 | Team structure and intake. |

## Archive

[`archive/`](archive/README.md) — **nothing in it is current.** Superseded material kept in full so
the reasoning survives: the phase-1 planning corpus, the shipped phase-2 feature plans, the retired
git-core task plans, point-in-time reports and walkthroughs, and the completed rebrand plan. Read it
for provenance, never as a spec.

## Where does a new doc go?

Put it in the matching folder above. A loose new `docs/*.md` is drift — the two that exist
(`security-architecture.md`, `mainguardos-updates.md`) are long-standing exceptions, and `STATUS.md`
is deliberately at the top level because it is the page people need first.

**When a doc is superseded or its work ships, move it to `archive/` and add a one-line status note
at the top** saying what replaced it and when. Don't delete it, and don't leave it in place looking
current — a shipped plan that still reads as a plan is how a new contributor ends up building
something twice.

Relative links between docs are checked: keep them resolving when you move a file.
