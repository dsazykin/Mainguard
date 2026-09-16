# Mainguard — Backlog & Deferred Work

The single source of truth for **what's not built yet**. Two parts:

1. **§A — New backlog features** — net-new features proposed and deferred (not started). Each has enough of a
   sketch to pick up directly, in the same offline-slice style as the archived
   `docs/archive/feature-plans/git-core/T-*.md` plans.
2. **§B — Deferred within shipped features** — the parts of already-merged features that were intentionally
   left for a human (live host-account matrices, unfinished gestures, visual polish). These are indexed
   against `Mainguard_User_Testing_Guide.md` so nothing is lost.

_Last updated: 2026-07-07._

---

## A. New backlog features (not started)

Deferred by the user as "later, not urgent." Priority order is a suggestion, not a commitment.

> **Update 2026-07-07:** A-1…A-3 are now formally scheduled as the **client-parity track
> (P2-C1…P2-C3)** in `Mainguard_Master_Implementation_Document_v2.md` (on the `phase2`
> branch) — same sketches, elevated to competitive-parity priority; the multi-repo
> dashboard additionally becomes the swarm control surface.

### A-1 — Interactive bisect assistant
**Why:** `git bisect` is the fastest way to find the commit that introduced a bug, but its CLI is fiddly and
easy to abandon mid-run. A guided UI (mark good/bad, auto-checkout the next candidate, show progress, land on
the culprit) makes it approachable — and is a natural seam for an agent to drive.
**Sketch (mostly local, CLI-driven → largely offline-verifiable, like T-29):**
- Core (CLI via `RunGitChecked`, state via `ExecuteWithRepo`): `StartBisect(repoPath, badSha, goodSha)`,
  `MarkGood`/`MarkBad`/`MarkSkip` (each returns the next candidate + a `BisectState`: remaining commits,
  steps left, current sha, done+culprit), `ResetBisect` (abort → restore HEAD). A pure helper to parse
  `git bisect` porcelain / `BISECT_LOG`.
- UI: a Bisect panel/wizard — pick the known-bad (default HEAD) + known-good (a tag/older commit), then a
  big **Good / Bad / Skip** control per step with a progress bar ("~N steps left"), auto-checkout, and a
  **culprit** result card (the commit + its blame/PR context via T-32) with **Abort**.
- Journaled via T-19 where it moves HEAD; graceful on a dirty tree (refuse, like undo).
- **Offline-verifiable:** a local fixture repo with a known bug-introducing commit → drive good/bad → assert
  the reported culprit. Deferred: nothing external (fully local).
**Depends on:** T-19 (journal), T-32 (culprit → PR context, optional). No host account.

### A-2 — Global fuzzy search
**Why:** One search box to jump to anything — a commit (message/sha/author), branch, tag, file, PR, or issue —
turns navigation from menu-diving into typing. Reuses the T-18 `FuzzyMatcher`, so ranking is already solid.
**Sketch (mostly local + reuses host services → offline-verifiable):**
- Core: an `ISearchAggregator` that fans a query out to sources — local (commits via the graph walk, branches/
  tags via existing reads, files via the working tree/index) and, when a token is present, host (PRs via T-23,
  issues via T-24). Each source returns ranked `SearchHit`s (kind, title, subtitle, jump target) scored by the
  pure `FuzzyMatcher`; merge + globally re-rank; cap + debounce.
- UI: a global search overlay (e.g. `Ctrl+Shift+F` or extend the T-18 palette with a "search everything" mode)
  — grouped results (Commits / Branches / Tags / Files / PRs / Issues) with highlighted match spans; Enter
  jumps (open the commit in the graph, the file in the diff/blame, the PR/Issue panel).
- **Offline-verifiable:** local sources + host sources against fixtures; the fuzzy ranking is already pinned.
  Deferred: live host result freshness (needs a token) — the local slice is fully testable.
**Depends on:** T-18 (`FuzzyMatcher`), T-23/T-24 (host sources, optional). Local slice needs no host account.

### A-3 — Multi-repo dashboard
**Why:** Polyrepo/microservice work means juggling many repos; today Mainguard opens one at a time. A dashboard
showing several repos side-by-side (branch, ahead/behind, dirty-state, last-fetched, quick actions) is a
power-user headline and the control surface an agent swarm would monitor.
**Sketch (local aggregation → offline-verifiable):**
- Core: a `WorkspaceOverviewService` that, for a set of registered repo paths, reports a compact per-repo
  status (current branch, ahead/behind vs upstream, dirty/clean, stash count, last-fetched) via
  `ExecuteWithRepo` — cheap reads, cached, refreshed on `RepositoryChanged`/auto-fetch.
- UI: a dashboard grid/list of repo cards (status pills + quick **Fetch/Pull/Open**); click → open that repo
  in the full workspace. Persist the tracked-repo set (already have repo bookmarks in the DB).
- Concurrency: reuse the T-10 `AutoFetchService` cadence across the set; per-repo overlap guard already exists.
- **Offline-verifiable:** local fixtures with known ahead/behind + dirty states → assert the reported status.
  Deferred: nothing external.
**Depends on:** T-10 (auto-fetch), existing repo persistence. No host account.

---

## B. Deferred within shipped features (already merged; parts left for a human)

Indexed to `Mainguard_User_Testing_Guide.md`. The code for each shipped feature is complete + machine-verified;
these are the intentionally-deferred slices. See the Overnight Report + the `// TODO(T-xx human-review)`
markers in code for finish notes.

### B-1 — Genuinely unfinished (feature gaps) — ✅ BOTH NOW DONE
| Feature | Status | Pointer |
|---|---|---|
| T-09 graph | **drag-to-rebase gesture** ✅ built (T-09b, PR #69) — inline ref chips + press-drag flyout | Guide §6.5 |
| T-13 diff | **image-diff swipe/onion-skin control** ✅ built (T-13b, PR #68) + bugs fixed (#86) | Guide §10.3 |

_(No remaining hard feature gaps — everything else below is live-account acceptance or polish.)_

### B-2 — Live host-account acceptance (need a real token / network; verify in a batch)
All are machine-verified against JSON fixtures + token-security-audited (token only in the `Authorization`
header). What's left is exercising the live path against a real account.

| Area | Guide § | Marker |
|---|---|---|
| Remotes push / force-with-lease / auto-fetch | §7.2, §7.3 | — |
| Multi-host auth (GitHub/GitLab device flow, Bitbucket/AzDO PAT, SSH) | §11.2 | GitLab needs a registered OAuth app id (placeholder) |
| Submodule real-remote init/update | §13.2 | — |
| Git LFS real-remote pull/prune | §14.2 | — |
| PR create/list/merge/close | §20.2 | `TODO(T-23 human-review)` |
| Issues list/create/comment/close | §21.2 | `TODO(T-24 human-review)` |
| PR review submit (approve/request-changes/comment) | §22.2 | `TODO(T-25 human-review)` |
| CI checks live fetch + re-run | §23.2 | `TODO(T-26 human-review)` |
| Notifications live fetch + mark-read | §24.2 | `TODO(T-27 human-review)` |
| Release live publish | §25.2 | `TODO(T-28 human-review)` |
| PR checkout-into-worktree (real GitHub PR) | §26.2 | `TODO(T-29 human-review)` |
| Blame → PR/issue live resolve | §29.2 | `TODO(T-32 human-review)` |

### B-3 — Visual / feel polish (cosmetic; non-blocking)
| Feature | Polish item | Guide § |
|---|---|---|
| T-11 blame | gutter metrics + heat-ramp/contrast across 5 themes; live recolor on theme switch | §8.2 |
| T-15 signing | signed-badge glyph/size/placement (crowds the message) | §12.2 |
| T-21 clone | progress-bar animation easing | §18.3 |
| T-22 analytics | chart legibility in CommandDeck / Atelier / LoomAurora (lane-hue lightness overlap) | §19.2 |
| T-25 PR review | verdict picker shows raw enum names (Comment/Approve/RequestChanges) | §22.2 |
| T-29 checkout | crowded PR-row action strip | §26.2 |
| all | per-section theme sweeps | throughout |

### B-4 — Optional enhancements flagged but not in any contract
- **T-22 analytics:** per-HEAD SQLite result caching + `IProgress` live-streaming of partial results (Guide §19).
