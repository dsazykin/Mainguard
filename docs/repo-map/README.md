# Repository Map — where things live

The per-file index of the whole repo, split by project. It exists so an agent can find code
**without re-scanning the tree**, and so that a 460 KB entry point doesn't have to be read to
answer "where does this go?".

**Keep it current: whenever you add, move, rename, or delete a file, update its entry in the
matching file below, in the same change.** A new file without a map entry is an incomplete change.

## Which file do I open?

Start from the path you are about to touch.

| If you are working on… | Open |
|---|---|
| `Mainguard.Git/` — git engine, EF/`AppDbContext`, services, providers, analytics, graph | [`mainguard-git.md`](mainguard-git.md) |
| `Mainguard.Agents/` — orchestration, sandbox/Docker, adapters, terminal, daemon seams | [`mainguard-agents.md`](mainguard-agents.md) |
| `Mainguard.Protos/` — the gRPC contract, codegen | [`mainguard-protos.md`](mainguard-protos.md) |
| `Mainguard.Server/` — the daemon (gRPC host, interceptors, services) | [`mainguard-server.md`](mainguard-server.md) |
| `Mainguard.UI/` — design-system base, `Themes/*.axaml`, edition seams | [`mainguard-ui.md`](mainguard-ui.md) |
| `Mainguard.App.Shell/`, `Mainguard.Agents.UI/`, `Mainguard.Client.App/`, `Mainguard.Pro.App/` — Views, ViewModels, the exe heads | [`mainguard-app-shell.md`](mainguard-app-shell.md) |
| `Mainguard.Tests/`, `Mainguard.Server.Tests/`, test tooling, harnesses | [`tests-and-tooling.md`](tests-and-tooling.md) |
| `installer/` — OOBE driver, elevated helper, uninstaller | [`installers.md`](installers.md) |
| `images/`, `site/`, `docs/security-architecture.md` | [`images-and-site.md`](images-and-site.md) |

### By directory — the git/agents boundary

These two assemblies are the pair most often confused, so the split is listed explicitly. It is
also the rule that decides where **new** code goes.

| Lives in `Mainguard.Git/` | Lives in `Mainguard.Agents/` |
|---|---|
| `Actions/` `Analytics/` `Audit/` `Checks/` `Commits/` `Exceptions/` `Graph/` `Hosting/` `Http/` `Issues/` `Migrations/` `Models/` `Notifications/` `PullRequests/` `Releases/` `Review/` `Safety/` `Security/` `Services/` `Sync/` | `Agents/` `Daemon/` `Terminal/` |
| root `AppDbContext.cs` `MainguardPaths.cs` | `Services/{I,}ForegroundMergeService.cs`, `Services/ExternalPrMergeService.cs` |

Prefer putting new **pure git/persistence** logic in `Mainguard.Git`, and new **agent-platform**
logic in `Mainguard.Agents`. Keep `Mainguard.App.Shell` and the exe heads thin.

## Why the two assemblies split (ADR-0001, step 2a)

**Step 2a (ADR-0001) split this section across two assemblies.** The pure **git engine + persistence**
moved to **`Mainguard.Git/`** (root namespace `Mainguard.Git.*`; a clean leaf carrying LibGit2Sharp / EF
Core / DiffPlex / DataProtection, with NO Docker.DotNet / Porta.Pty / gRPC / agent dependency):
`MainguardPaths`, `AppDbContext` + `Migrations/`, `Services/` (every service EXCEPT
`ForegroundMergeService`), `Models/`, `Analytics/`, `Graph/`, `Commits/`, `Hosting/`, `Http/`,
`PullRequests/`, `Issues/`, `Checks/`, `Notifications/`, `Releases/`, `Review/`, `Safety/`, `Security/`,
`Sync/`, `Audit/`, `Actions/`, `Exceptions/`. What **lives in `Mainguard.Agents/`** — the agent platform
(renamed in step 2b) — is `Agents/`, `Daemon/`, `Terminal/`, and `Services/ForegroundMergeService`;
`Mainguard.Agents` references `Mainguard.Git` and keeps Docker.DotNet + Porta.Pty. `Mainguard.Git`
grants `Mainguard.Agents`/`Mainguard.Tests` `InternalsVisibleTo` for the low-level git helpers
(`GitService.RunGit`, the shared `RepoSlug`, …). The per-file bullets below are otherwise unchanged —
read each moved path (`MainguardPaths`, `AppDbContext`, `Services/`, `Models/`, the provider dirs, …) as
`Mainguard.Git/…`.

## Conventions in these files

- Each file opens with the project's per-directory entries, then a **Role in the solution** section
  holding that project's summary (what used to be AGENTS.md's *Solution Layout* bullet).
- Entries are nested bullets: a directory, then one sub-bullet per file or file group.
- Lines are hard-wrapped at ~100 columns. Keep it that way — the previous single-line-per-directory
  format reached 43,000 characters on one line, which is why nobody noticed it growing.

---

Back to [`AGENTS.md`](../../AGENTS.md)
