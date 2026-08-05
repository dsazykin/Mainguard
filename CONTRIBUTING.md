# Contributing to Mainguard

Welcome. This guide is the **on-ramp**; [`AGENTS.md`](AGENTS.md) is the **source of truth** for architecture, conventions, the design system, and the repository map — read it before your first change, and when this guide and AGENTS.md disagree, AGENTS.md wins (and fixing the drift is part of your change).

## Get building

Prerequisites: the .NET SDK pinned in `global.json` (10.0.100, `latestFeature` roll-forward — plain `dotnet` picks it up), Git, and for the GUI a desktop OS (Avalonia).

```bash
dotnet restore
dotnet build                         # the whole solution — run after any change
dotnet test                          # full xUnit suite (~1,100 tests, a few minutes)
dotnet run --project Mainguard.App.Shell     # launch the app

# focused runs while iterating
dotnet test --filter "FullyQualifiedName~CommitGraphRouterTests"
dotnet test --filter "FullyQualifiedName~GitServicesTests&Name=<MethodName>"
```

Docker wrappers reproduce the exact toolchain for build/test/EF only (not the GUI): `docker compose run --rm build|test|shell`.

**Gotcha:** `dotnet build` failing with `MSB3021 … apphost.exe … in use` means a `Mainguard.App.Shell` instance is still running and holding the output exe — that's a file lock, not a code error. Close the app, rebuild.

## Where code goes

Three projects in `Mainguard.slnx` (a `.slnx`, not `.sln`):

| Project | What belongs there |
|---|---|
| **`Mainguard.Agents`** | All logic: git operations, models, analytics, the pure engines, EF Core persistence. Interface-first services (`IGitService`/`GitServices`, …). **No UI dependency — ever.** When in doubt, put logic here. |
| **`Mainguard.App.Shell`** | Thin Avalonia UI. `ViewModels/` ↔ `Views/` paired 1:1 by name (wired via `ViewLocator`). ViewModels hold state and call Core; Views hold layout and tokens. No colors, no business rules in ViewModels. |
| **`Mainguard.Tests`** | xUnit, against Core and ViewModels. `Fixtures/TempRepoFixture` builds disposable real repos; `Headless/*RenderHarness` render real views in all five themes. |

`Mainguard.StyleConsole`, `Mainguard.StyleTests`, `Mainguard.AvaloniaTests` are scratch — not in the solution, don't build on them.

## The five rules reviewers will actually reject on

1. **LibGit2Sharp only through `IGitService.ExecuteWithRepo(...)`.** It is the single audited place a repo handle is opened/disposed, and it owns the index.lock retry (ADR-001 in `docs/phase-2/ADRs.md`). A raw `new Repository(...)` anywhere else leaks native handles and re-creates the `.git/index.lock` collisions this app exists to prevent.
2. **No raw colors in UI.** Bind semantic tokens with `{DynamicResource …}` (never `StaticResource` for colors — it won't follow live theme switches). Pick a `Button.*`/`Border.*` component class by role. A new token goes into **every** `Themes/*.axaml` (five themes — Midnight Loom, Daylight Loom, Command Deck, Atelier, Loom Aurora). Never assume dark: Daylight Loom is light, and every surface must read in all five. Custom-drawn controls resolve tokens at render time and invalidate on `ThemeManager.ThemeChanged` (see `CommitGraphCanvas`).
3. **Keep the [`docs/repo-map/`](docs/repo-map/README.md) index current.** Create/move/rename/delete a file → update its entry in the matching per-project file in the same change. An unindexed file is an incomplete change.
4. **Caches are bounded and typed errors only.** Any cache states its bound (LRU + capacity — see `BlameCache`/`CommitStatsCache`); failures reaching a ViewModel are typed (`GitOperationException` etc.), never raw library exceptions. Secrets/tokens never appear in argv, URLs, logs, or exception text.
5. **Don't commit to `main`.** Branch, PR, review. v1 client fixes target `main`; agent-platform work targets `phase2` (see the branching section in AGENTS.md).

## Testing conventions

- **Real repos, not mocks, for git behavior** — `TempRepoFixture` creates and disposes actual repositories; helpers commit with pinned authors/dates so assertions are deterministic on any CI.
- **Pure engines get pinned examples + property tests.** Examples pin exact output; property tests (`PureEnginePropertyTests`, seeded `System.Random`, no external framework — ADR-005) pin the laws: round-trips, conservation, nothing-dropped. A known model limit is *pinned in a named test*, never silently avoided (ADR-006).
- **No timing asserts in xUnit.** Perf-sensitive tests assert structure and *print* measurements (`[H1]`, `[H2]` tags); enforcement belongs to the future benchmark project (ADR-007, Hotspot Register [PERF-2]).
- **UI changes get a render-harness pass** where one exists (`Mainguard.Tests/Headless/`) — they render the real view in all five themes and write PNGs to `artifacts_headless/`.
- Run `dotnet test` before handing anything back; run the focused class while iterating.

## Commit & PR etiquette

- Conventional-commit style subjects: `type(scope): summary` — `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `chore`. The app's own T-31 composer follows commitlint; so do we.
- One concern per commit; the body says *why*, not *what the diff already shows*.
- Significant engineering decisions get an ADR in `docs/phase-2/ADRs.md` (Context → Decision → Consequences); amend by adding, never by rewriting history.
- If you change how the app actually works, update `README.md`; if you change a plan, update the matching `docs/` file (the taxonomy is in AGENTS.md).

## Orientation shortcuts

- **"Where is X?"** — [`docs/repo-map/`](docs/repo-map/README.md) is the per-file index (its README routes you to the right project file); grep second.
- **The design system** — `docs/design/DesignSystem.md` (tokens, shape/spacing/type scales, icon gates); voice and strings — `docs/creative/`.
- **Why is it built this way?** — `docs/phase-2/ADRs.md` for recorded decisions; `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` is the binding phase-2 spec.
- **Performance budgets** — `docs/phase-2/Mainguard_Performance_Hotspot_Register.md`; H1–H3 govern the client paths you're most likely to touch.
