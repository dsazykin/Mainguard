# T-22 — Analytics Completion — Implementation Plan

**Task ID:** T-22 · **Milestone:** M5 · **Priority:** P2 · **Depends on:** nothing.
**Branch:** `plan/T-22-analytics` → implement on `feat/T-22-analytics` off `main`.

> **Source of truth:** §T-22 of the Master Doc + strategy §E-5.1–5.4, §TI-22 of the Test Strategy.
> This is the final Git-client task before the roadmap turns to the agentic phase.

---

## 0. Context

Analytics scaffolding exists (`Mainguard.Agents/Analytics/RepositoryAnalyzer.cs`, `AnalyticsViewModel`). This
task **completes** it: make the analyzer gitignore-aware, always skip `.git/`, honor a `CancellationToken`,
and finish churn + punch-card metrics.

### What you can rely on

| Fact | Where |
|---|---|
| `RepositoryAnalyzer` walk + `AnalyticsViewModel` scaffolding | `Core/Analytics/RepositoryAnalyzer.cs` |
| `repo.Ignore.IsPathIgnored(relPath)` for gitignore awareness | LibGit2Sharp |
| Commit walk + `DateTimeOffset` timestamps for punch-card | `GitServices.GetRecentCommits` |

---

## 1. Files to modify / create

| Action | Path |
|---|---|
| **Edit** | `Core/Analytics/RepositoryAnalyzer.cs` — gitignore-aware walk, `.git/` skip, `CancellationToken`, churn/punch-card |
| **Edit** | `AnalyticsViewModel` — cancellation, render churn + punch-card |
| **Create** | `Mainguard.Tests/RepositoryAnalyzerTests.cs` |

---

## 2. Implementation

- **Gitignore-aware walk:** skip any path where `repo.Ignore.IsPathIgnored(rel)` is true, **cached per-dir**
  (evaluate the directory once, not per file). Honor negations (`!keep.js`) — LibGit2Sharp's `IsPathIgnored`
  already does. **Always skip `.git/`** unconditionally.
- **Cancellation:** thread a `CancellationToken` through the walk; check it periodically and return promptly
  when cancelled (TI-22 asserts a prompt return on a large synthetic tree).
- **Churn:** per-file added/removed line counts over the history window.
- **Punch-card:** bucket commits by (weekday, hour) from each commit's `DateTimeOffset` — bucketing must be
  exact for scripted timestamps (tests use **fixed** `DateTimeOffset`s, never `Now`).

---

## 3. Test contract — TI-22 (`RepositoryAnalyzerTests.cs`)

- analyzer on a fixture with an ignored `node_modules/` (+ a `!keep.js` negation) counts **exactly** the
  non-ignored bytes; `.git/` always skipped;
- **cancellation honored** — start on a large synthetic tree, cancel, assert prompt return;
- **punch-card bucketing exact** for scripted commit timestamps (fixed `DateTimeOffset`s).

---

## 4. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~RepositoryAnalyzer"
grep -n "IsPathIgnored" Mainguard.Agents/Analytics/RepositoryAnalyzer.cs   # gitignore-aware
```

- [ ] Gitignore-aware (per-dir cached) walk; `.git/` always skipped; negations honored.
- [ ] `CancellationToken` honored with prompt return.
- [ ] Churn + punch-card metrics, exact bucketing on fixed timestamps.
- [ ] TI-22 green. One PR linking **T-22**.

---

## 5. Milestone note

T-22 closes M5 and the **Git-client scope** of `Mainguard_Master_Implementation_Document.md`. Everything after
this (Phases 6–9, F6 BYOK, G-7.x daemon/terminal/sandbox/swarm, H-8.x governance, I cloud worktrees,
J installer, K Vibe mode) is the **agentic development app** and is intentionally **out of scope** here — it
is specified only at strategy level in `Mainguard_Implementation_Strategy.md` and, per §5 of the Master Doc,
receives its own contract/invariant/step treatment in a **v2** of the master document once M3 completes and
the daemon spike starts. **Do not begin those from the strategy doc alone.**
```
