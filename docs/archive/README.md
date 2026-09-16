# Archive — superseded docs, kept for history

**Nothing in this folder is current.** Read it for provenance — why a decision was made, what an
earlier plan said — never as a specification. If you need to know how the product works today,
start at [`../README.md`](../README.md).

This folder was previously named `obsolete docs/`. The literal space in that name broke relative
links, which is why it was renamed rather than left alone.

## What is in here

| | |
|---|---|
| `feature-plans/git-core/` | The retired git-core task plans `T-02`…`T-32` (merge chunker, conflict plumbing, interactive rebase, blame, LFS, submodules, PR review, …). Superseded by the phase-2 corpus in [`../phase-2/`](../phase-2/) and [`../feature-plans/`](../feature-plans/). |
| `Mainguard_Feature_Plan_Triage.md` | The triage pass that retired the plans above. |
| `LaunchReserve.md` | Archived 2026-07-12. Supersession map is in its own header: §4 and §6 live on as Appendices C and D of the Voice & Delight Bible, and the launch copy moved to [`../creative/Narrative.md`](../creative/Narrative.md). |
| `prompt-bundles/` | One-off agent run scripts (the Fable lane bundles), formerly loose at the repo root. Working material, not documentation. |

## Adding to the archive

When a doc is superseded, **move it here and leave a supersession note in its header** saying what
replaced it and when. Don't delete it — the point of this folder is that the reasoning survives.
