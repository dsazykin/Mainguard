# GEMINI.md

This file provides guidance to Gemini (and the Gemini CLI) when working with code in this repository.

## Read AGENTS.md first

**[`AGENTS.md`](AGENTS.md) is the source of truth** for architecture, the design system, conventions, and git hygiene, and it is kept current with the code. Read it before changing anything. This file only adds operational specifics and highlights a few rules you must not miss. Where the two ever disagree, AGENTS.md wins — and fix the drift.

## What this is

A working native Git GUI (**.NET 10**, Avalonia 11 + MVVM via `CommunityToolkit.Mvvm`, `LibGit2Sharp`, SQLite/EF Core). The multi-agent / sandbox / terminal features in `README.md` and the roadmap docs are **planned, not built** — don't implement them into the code unless asked. The docs are the destination; the code is the current state.

Solution is `Mainguard.slnx` (a `.slnx`, not `.sln`). The app now ships as **two edition heads over one shared shell library** — a free Git client and a Pro build — so you run a *head*, never the shell. **[`AGENTS.md`](AGENTS.md) is the architecture source of truth; the per-file index lives in [`docs/repo-map/`](docs/repo-map/README.md)** — open its routing table and read only the project you are touching. The projects that matter most:

- **`Mainguard.Git`** — git engine + EF/`AppDbContext` + `IGitService` (the all-editions base; put git logic here).
- **`Mainguard.UI`** — design system + the five themes + the edition seams (`IEditionManifest` / `IAgentPlatformSurface`) + `ViewModelBase` + `ViewLocator`.
- **`Mainguard.Agents`** — agent platform (Docker.DotNet + Porta.Pty live here).
- **`Mainguard.Agents.UI`** — Pro-only Views/ViewModels + daemon client.
- **`Mainguard.App.Shell`** — the edition-agnostic shell **library** (MainWindow + git surfaces; **no entry point**).
- **`Mainguard.Client.App`** — the **free Git-client exe head** (references the shell only; its closure excludes the agent platform).
- **`Mainguard.Pro.App`** — the **Pro exe head** (shell + Agents.UI + the Mainguard OS payload + OOBE).
- **`Mainguard.Server`** / **`Mainguard.Protos`** — daemon + gRPC contract.
- **`Mainguard.Tests`** / **`Mainguard.Server.Tests`** — xUnit.
- **`installer/Mainguard.Installer{,.Elevated}`** / **`installer/Mainguard.Uninstall`** — installer + uninstaller.

`Mainguard.StyleConsole` and `Mainguard.StyleTests` are scratch — not in the solution, don't rely on them.

## Commands

```bash
dotnet restore
dotnet build                              # build whole solution — run after any change
dotnet test                               # all xUnit tests (run when you touch Mainguard.Git)
dotnet run --project Mainguard.Client.App # launch the free client head (no WSL)
dotnet run --project Mainguard.Pro.App    # launch the Pro head — a head, not the shell library

# a single test class, or one method by name
dotnet test --filter "FullyQualifiedName~CommitGraphRouterTests"
dotnet test --filter "FullyQualifiedName~GitServicesTests&Name=<MethodName>"
```

The SDK is pinned to `10.0.100` (`global.json`, `latestFeature` roll-forward) so `dotnet` picks the right toolchain automatically.

Docker wrappers reproduce the exact toolchain for **build/test/EF only** (not the GUI): `docker compose run --rm build|test|shell`.

### EF Core migrations

`dotnet-ef` is a local tool; the DB migrates on app startup (the shared shell's `App.axaml.cs`). After changing entities in `AppDbContext` (now in `Mainguard.Git`):

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project Mainguard.Git
```

Commit the migration + snapshot together; never hand-edit an applied migration.

### Build gotcha: close the app first

`dotnet build` fails with `MSB3021 … apphost.exe … being used by another process` if a running head (`Mainguard.Client.App` or `Mainguard.Pro.App`) is still open — it holds a lock on its output exe. That error is a lock, not a code error (XAML/C# already compiled). Close the running app and rebuild.

## Non-negotiable rules (details in AGENTS.md)

- **LibGit2Sharp only through `IGitService.ExecuteWithRepo(...)`** — it opens/disposes the native handle deterministically. Ad-hoc or long-lived `Repository` handles leak and cause `.git/index.lock` collisions (the exact bug this app exists to prevent).
- **No raw colors in UI.** Bind design tokens with `{DynamicResource …}` (never `StaticResource` for colors — it won't follow live theme switches). Pick a `Button.*` / `Border.*` component class by role instead of setting `Background`/`Foreground`. New tokens go in **every** `Themes/*.axaml`; new classes/icons go in `App.axaml`. There is one design system with five switchable color themes — never assume "dark" (Daylight Loom is light).
- **Keep the [`docs/repo-map/`](docs/repo-map/README.md) index current.** When you create/move/rename/delete a file, update its entry in the matching per-project file in the same change — an unindexed file is an incomplete change.
- **No DI container** currently: `App` exposes a static `Settings`; `MainWindowViewModel` is constructed directly. Follow the pattern.
- **Commit and push your OWN branch often; never merge, never touch `main` or `phase2` directly.** Commit at every meaningful sub-step — plumbing compiles, a decision is wired, tests go green, docs updated — and push each one. `wip:` subjects are expected for intermediate commits, and those carry **no `Co-Authored-By: Claude` / `Claude-Session:` trailers** (final commit only). Work that is not pushed does not exist: an afternoon of two agents' work was lost this way, unrecoverable. Verify with `dotnet build` (+ `dotnet test` for `Mainguard.Git`), open a PR, and leave the merge to the human — the review is the point. See **[`AGENTS.md`](AGENTS.md) § Branching & commits**.
- **Never build in `/tmp` — it is a `tmpfs`, i.e. RAM.** Worktrees and build output go on a real filesystem (`~/mg-work/<task>/`). A `No space left on device` from `/tmp` is a memory warning, not a disk one; filling it killed the WSL VM three times. Don't run the whole-solution `dotnet test` locally alongside other heavy work — use a `--filter`, CI runs the full suite.
