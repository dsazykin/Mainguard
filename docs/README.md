# Documentation — start here

Everything under `docs/` is planning, design, and analysis material. **The code is the current
state; most of these docs are the destination.** Where a doc and the code disagree about what
exists today, the code wins — see [`../AGENTS.md`](../AGENTS.md) *What Mainguard Is*.

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
| [`design/`](design/README.md) | 22 | UI/UX design output. `README.md` is the design hub — start there. |
| [`creative/`](creative/README.md) | 9 | Voice, brand, microcopy, motion. `README.md` is the brand hub; the Voice & Delight Bible is the law. Consult it before writing any user-facing string. |
| [`phase-2/`](phase-2/) | 12 | The active phase-2 agent-platform corpus. `implementation_plans/Mainguard_Master_Implementation_Document_v2.md` is the binding spec. |
| [`feature-plans/`](feature-plans/) | 16 | Per-task phase-2 feature plans (`P2-01`…`P2-22`). Subordinate to the master implementation doc. |
| [`review/`](review/) | 10 | Human review guides — what to review, how, and what "correct" looks like. |
| [`planning/`](planning/) | 8 | Roadmaps and older plans. Aspirational: the destination, not the current code. |
| [`adr/`](adr/) | 1 | Architecture Decision Records. Cross-cutting decisions, `Status → Context → Decision → Consequences`. |
| [`test_implementation_plan/`](test_implementation_plan/) | 2 | Test strategy and the manual user-testing guide. |
| [`reports/`](reports/) | 3 | Point-in-time snapshots: implementation reports, session handoffs, audit-log evidence. |
| [`rebrand/`](rebrand/) | 1 | The GitLoom → Mainguard rename, phased. Brand rules themselves live in `../PRODUCT.md` / `../DESIGN.md`. |
| [`security-architecture.md`](security-architecture.md) | 1 | The sandbox/daemon threat model and its controls. |
| [`mainguardos-updates.md`](mainguardos-updates.md) | 1 | The Mainguard OS payload update mechanism. |

## Business

Not needed to build or review code. Grouped so `docs/` opens with engineering material.

| Folder | Files | What is in it |
|---|---:|---|
| [`business/go-to-market/`](business/go-to-market/) | 26 | Market & GTM strategy, decks, marketing, sales. `GTM_Execution.md` is the execution hub. |
| [`business/market-analysis/`](business/market-analysis/) | 7 | Competitor and market research. |
| [`business/members/`](business/members/) | 3 | Team structure and intake. |

## Archive

[`archive/`](archive/) — 35 files. Superseded material kept in full for history: the retired
git-core feature plans (`T-02`…`T-32`), feature-plan triage, `LaunchReserve.md`, and
`prompt-bundles/` (one-off agent run scripts). **Nothing here is current.** Read it for
provenance, never as a spec.

## Where does a new doc go?

Put it in the matching folder above. A loose new `docs/*.md` is drift — the two that exist
(`security-architecture.md`, `mainguardos-updates.md`) are long-standing exceptions, not a
precedent. When a doc is superseded, move it to `archive/` and leave a supersession note in its
header rather than deleting it.

Relative links between docs are checked: keep them resolving when you move a file.
