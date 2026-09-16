# GEMINI.md

Guidance for Gemini (and the Gemini CLI) working in this repository.

**This file intentionally carries no rules of its own.** Everything lives in one place so there is
exactly one document that can go stale:

| Read | For |
|---|---|
| [`AGENTS.md`](AGENTS.md) | **The source of truth.** Architecture, the design system, conventions, git hygiene, and the non-negotiable rules. Read it before changing anything. |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Build, test, run — the on-ramp, including the `MSB3021` apphost-lock gotcha. |
| [`docs/README.md`](docs/README.md) | What is in `docs/`, and where a new doc goes. |
| [`docs/repo-map/README.md`](docs/repo-map/README.md) | Where any file lives, indexed per project. Use it instead of re-scanning the tree. |

Four rules are worth naming here because missing one is expensive — the full statement of each is
in `AGENTS.md`:

1. **LibGit2Sharp only through `IGitService.ExecuteWithRepo(...)`.** Ad-hoc handles leak and cause
   the `.git/index.lock` collisions this app exists to prevent.
2. **No raw colors in UI.** Bind design tokens with `{DynamicResource …}` and pick a component
   class by role. Four themes switch live — never assume "dark".
3. **Keep [`docs/repo-map/`](docs/repo-map/README.md) current** in the same change that adds,
   moves, renames, or deletes a file.
4. **Branch from and PR into `phase2`, not `main`.** Commit and push your own branch often; never
   merge, and never touch `main` or `phase2` directly.

If this file and `AGENTS.md` ever disagree, `AGENTS.md` wins — and fix the drift.
