# Mainguard Feature Plan Triage — Manual-Check Assessment

Reviewed all 21 `plan/T-0X-*` branches (each holding one `docs/feature-plans/T-0X-*.md` doc) against
`docs/planning/Mainguard_Master_Implementation_Document.md`. Per §3 of that doc, tasks build in order **T-02 → T-22**
(a task starts once its listed dependencies are merged; see §3 for the exact dependency graph).

**"No manual check needed?"** = can correctness be fully confirmed by `dotnet build` / `dotnet test` / the
plan's own grep checks alone, with no one needing to click through the running app.

| Branch | Feature | No manual check needed? | Why |
|---|---|---|---|
| T-02 | Merge chunker (pure engine) | Yes | Pure C#, algorithm given almost as pseudocode, 14 pinned unit tests, zero UI. |
| T-03 | Conflict index plumbing | Yes | Core-only, method bodies given nearly verbatim, existing test fixture. |
| T-04 | Conflict-resolver UI | No | Full rewrite of a working feature; the plan's own reviewer script has an explicit "Manual (scripted repo)" step. |
| T-05 | Tag management | Yes | CRUD fully testable; tag chips/dialog visuals are a minor judgment call but low risk. |
| T-06 | Partial-staging UI | Partial | `PatchParser`/`PatchBuilder` are pure and pinned, but the drag-select hunk/line UI is real interactive design that needs driving to confirm. |
| T-07 | Worktree porcelain + commit diffs | Yes | CLI wrapper + exact porcelain-parser rules, local-fixture integration tests only. |
| T-08 | Interactive rebase (completion) | Yes | Mostly test-backfill on code that already works; UI "tightening" is verify-only against existing behavior. |
| T-09 | Graph interactions | Partial | Hit-tester is pure/testable; drag-drop flyout + context-menu UX is genuinely under-specified. |
| T-10 | Remotes mgmt, auto-fetch, push opts | Yes | Service + background timer fully testable; remotes UI is plain forms. |
| T-11 | Blame | Partial | Service/cache testable; AvaloniaEdit gutter rendering wants a look. |
| T-12 | File history | Yes | Three small Core methods (code given) + a plain list/diff view. |
| T-13 | Diff quality | Partial | Intra-line/whitespace logic is pinned; the image-diff swipe control is a new custom control with little detail. |
| T-14 | Multi-host auth UI + SSH manager | No | Plan explicitly calls for a "Manual matrix… not automated": real GitHub/GitLab OAuth, Bitbucket/AzDO PAT flow, SSH passphrase, argv-secret-leak check. **Skip when reached.** |
| T-15 | Commit/tag signing | No | Tests are `RequiresGpg`-gated — needs real GPG/SSH signing tooling that may not be present. **Skip when reached.** |
| T-16 | Submodules | Yes | CLI-driven, pure status mapper, local-only fixtures (no real network). |
| T-17 | Git LFS | No | Tests are `RequiresGitLfs`-gated — without the binary, most of the suite just skips, so correctness can't be proven. **Skip when reached.** |
| T-18 | Command palette & shortcuts | Yes | Bulk of the work (`FuzzyMatcher`, `ActionRegistry`, `ShortcutMap`) is pure and pinned; palette overlay is simple. |
| T-19 | Undo journal | Partial | Round-trip tests are thorough, but it's a sweeping refactor touching every mutating method — easy to miss one op kind on a first pass. |
| T-20 | Reflog viewer | Yes | Tiny contract, simple read-only list + two well-specified actions. |
| T-21 | Profiles / worktree UI / clone progress | Partial | Profile-apply and cancel-delete are testable; a live progress bar is inherently a "look at it" feature. |
| T-22 | Analytics completion | Partial | Analyzer logic fully testable; churn/punch-card chart rendering is visual. |
| T-23 | Pull/Merge request integration | No | Host-account-gated like T-14: the host-agnostic service + GitHub provider + JSON-fixture parsing tests are offline-verifiable, but create/list/merge against a real GitHub/GitLab/etc. account is a manual matrix. **Skip when reached; revisit with credentials.** |

## Notes

- **Skip on reach:** T-14, T-15, T-17, T-23 are blocked on external accounts/tooling (real GitHub/GitLab/Bitbucket/
  Azure DevOps for T-14 and T-23; GPG for T-15; the `git-lfs` binary for T-17). Per direction, these are skipped when
  the sequence reaches them and revisited later once those dependencies are available. T-23's offline-verifiable
  slice (host-agnostic service + GitHub provider + parsing tests) may still be built ahead of the live matrix.
- Build order and per-task dependencies are authoritative in `docs/planning/Mainguard_Master_Implementation_Document.md`
  §3 — consult it before starting any task out of sequence.
