# Archive — superseded docs, kept for history

**Nothing in this folder is current.** Read it for provenance — why a decision was made, what an
earlier plan said — never as a specification. For how the product works today, start at
[`../README.md`](../README.md).

This folder was previously named `obsolete docs/`. The literal space in that name broke relative
links, which is why it was renamed rather than left alone.

## What is in here

| | |
|---|---|
| [`feature-plans/phase-2/`](feature-plans/phase-2/) | The 16 phase-2 feature plans `P2-01`…`P2-22` (BYOK key store, daemon gRPC, terminal PTY, sandbox hardening, AI gateway, agent lifecycle, merge queue, review cockpit, external PR intake, plan approval, installer). **All of them shipped** — each was verified against the code before archiving. The binding spec is [`../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md`](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md). |
| [`feature-plans/git-core/`](feature-plans/git-core/) | The retired git-core task plans `T-02`…`T-32` (merge chunker, conflict plumbing, interactive rebase, blame, LFS, submodules, PR review, …). Superseded by the phase-2 corpus. |
| [`planning/`](planning/) | The phase-1 planning corpus (July 2026): the git-foundation audit, implementation strategy, the v1 master implementation document, the v1 roadmap and implementation plan, the product-editions sequencing plan (shipped — see [`../adr/0001-product-editions.md`](../adr/0001-product-editions.md)), and the agent-image/daemon-logging backlog (both items marked ADDRESSED 2026-07-18). Superseded by the phase-2 corpus; the live backlog is [`../planning/Mainguard_Backlog.md`](../planning/Mainguard_Backlog.md). |
| [`reports/`](reports/) | Point-in-time snapshots: the 2026-07-07 overnight report, the 2026-07-06 session handoff, and the P2-15 audit-log implementation evidence (that work shipped). |
| [`walkthroughs/`](walkthroughs/) | Dated hands-on review sessions with screenshots and issue logs (2026-08-20, Windows 2026-08-24). The living procedures are in [`../review/`](../review/). |
| [`Mainguard_Test_Implementation_Strategy.md`](Mainguard_Test_Implementation_Strategy.md) | The v1 test contract, keyed to the v1 `T-NN` tasks. Superseded by [`../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md`](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md). |
| [`Mainguard_Rebrand_Plan.md`](Mainguard_Rebrand_Plan.md) | The phased rename plan. The rename landed — the repo, the solution and every identifier are `Mainguard.*` — so the document is history. Note it is self-inconsistent: a later blanket rename pass rewrote the old product name inside it, so its title now reads as a no-op, and the theme renames it proposed were never applied. |
| [`Mainguard_Feature_Plan_Triage.md`](Mainguard_Feature_Plan_Triage.md) | The triage pass that retired the git-core plans above. |
| [`LaunchReserve.md`](LaunchReserve.md) | Archived 2026-07-12. Supersession map is in its own header: §4 and §6 live on as Appendices C and D of the Voice & Delight Bible, and the launch copy moved to [`../creative/Narrative.md`](../creative/Narrative.md). |
| [`prompt-bundles/`](prompt-bundles/) | One-off agent run scripts (the Fable lane bundles), formerly loose at the repo root. Working material, not documentation. |

## Adding to the archive

When a doc is superseded — or the work it specifies ships — **move it here and add a one-line
status note at the top** saying what replaced it and when. Don't delete it; the point of this folder
is that the reasoning survives. Don't leave it in place either: a shipped plan that still reads as a
plan is how a new contributor ends up building something twice.
