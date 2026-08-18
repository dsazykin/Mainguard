# AGENTS.md

Guidance for humans and AI coding agents working in the Mainguard repository. Read this before making changes.

## What Mainguard Is

Mainguard is a premium, natively-rendered Git GUI (Avalonia + `LibGit2Sharp`) evolving into a multi-agent control center for orchestrating swarms of autonomous coding CLIs. **Today the codebase is a working Git client**; the swarm/sandbox/terminal features described in the roadmap are planned, not yet built. Keep that distinction in mind — the planning docs are the *destination*, the code is the *current state*.

## Documentation map — where things live (and where new docs go)

All planning/design/analysis docs live under `docs/`, grouped by purpose. **Put a new doc in the matching folder** so the tree stays navigable — an unsorted top-level `docs/*.md` is drift to fix.

| Folder | What goes here |
|---|---|
| **`docs/repo-map/`** | **The per-file index of the whole repo, split by project — start at its `README.md` routing table.** This is where a Repository Map entry goes when you add, move, or delete a file. |
| `docs/phase-2/` | The active phase-2 agent-platform corpus: **`implementation_plans/Mainguard_Master_Implementation_Document_v2.md` — the binding spec** (where any subordinate doc disagrees, the master doc wins; since the 2026-07-12 consolidation its UI task sections carry **Design decisions (binding)** blocks linking the design hub, and its baseline/invariants reference the Lane-H pass + ADRs), plus `implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md`, plus the Orchestration Protocol Spec, Environment Substrate Contract, WSL2 substrate, Uplift Study, RedTeam plan, Cloud/Vibe companion, Performance Hotspot Register, Risk Register, the Phase-2 Test Implementation Strategy, and **`ADRs.md`** — the client/non-security Architecture Decision Records (Context → Decision → Consequences; ADR-001 handle discipline + index.lock retry, ADR-002 per-HEAD analytics cache, ADR-003 router optimization, ADR-004 diff theme/emphasis policy, ADR-005 property-testing approach, ADR-006 merge blank-line conservation + known limit, ADR-007 bench-vs-test split, ADR-008 the macos-host substrate). |
| `docs/planning/` | Roadmaps and older plans: `Mainguard_Roadmap.md`, `Implementation_Plan.md`, `Mainguard_Backlog.md`, the v1 Master Implementation Document, feature-plan triage, git audit, implementation strategy (aspirational — the destination, not the current code). `Agent_Image_Provisioning_And_Daemon_Logging_Backlog.md` — the two structural gaps left open by the 2026-07-17 update-mechanism work (#193–#202): shipping `mainguard-agent-base` to installed VMs, and giving `mainguardd` real journal logging (field evidence, candidate approaches, acceptance criteria). **`Product_Editions_And_Structural_Sequencing.md`** — the plan to separate the three products (free Client / Pro / Cloud) by **edition composition on one trunk** (an `IEditionManifest` seam + per-edition packaging lanes), not by branch; §7 sequences it against the rename and argues the project rename (rebrand Phase 3) and the assembly split should be **one fused merge-freeze operation**. |
| `docs/adr/` | Repo-wide **Architecture Decision Records** — cross-cutting product/structure/release decisions, standard Status → Context → Decision → Consequences → References headings. **`0001-product-editions.md`** — separate the free **Client** / **Pro** / **Cloud** products by *edition composition on one trunk* (a static `IEditionManifest`/`App.Edition` seam mirroring `App.Settings`, Pro UI moved to a `Mainguard.Agents.UI` assembly behind two thin exe heads, per-edition Velopack lanes, CI reference-graph/twin-harness/manifest gates), never by branch or `#if`; fuses the `Mainguard → Mainguard` rename with the assembly split into one merge-freeze op and retires `phase2` for trunk-based dev. Distinct register from `docs/phase-2/ADRs.md`, which is the client/non-security **Lane-H code-quality** ADR log. |
| `docs/test_implementation_plan/` | Test strategy (v1) and the manual `Mainguard_User_Testing_Guide.md`. |
| `docs/reports/` | Point-in-time status snapshots (overnight reports, session handoffs). |
| `docs/design/` | UI/UX design output. **`README.md` is the design hub — start there** (authority ladder, reading order, per-surface cross-index; consolidated 2026-07-12). Chapters: foundation spec **`DesignSystem.md`** (the Lane A foundation — decoupled `Lane1`–`Lane5` palette per theme with gates G1–G5, the state-encoding icon system with gates E1–E4: signature solid/hollow/fractured shield triad, octagon/triangle/circle severity triad, solid-vs-hollow diff bars). **`SurfaceDesigns.md`** (Lane B Part 1 — the elevated designs for the five core surfaces: review cockpit / `RepoDashboardView`, commit graph / `CommitTimelineView` + `CommitGraphCanvas`, diff viewer / `DiffViewerView`, staging panel / `StagingPanelView`, and the OOBE/first-run path / `MainWindow` + `CloneDashboardView` — per surface the IA, primary flow, empty/loading/error states, the one signature accent, the five-theme reading, and an implementer delta list in its Appendix A; conforms to DesignSystem.md and takes its strings from `docs/creative/Microcopy.md`). **`FeatureDesigns.md`** (Lane B Part 2 — the client-parity features P2-C1…C5 designed end-to-end as experiences: the interactive bisect assistant (session strip on `CommitTimelineView` + Good/Bad/Skip + culprit card), the global fuzzy search overlay (`Ctrl+Shift+F`, grouped/highlighted, keyboard-first), the multi-repo Repositories home + cross-repo Needs-attention lane (`WorkspaceOverviewView` ledger, explicitly rows-not-card-grid), the split-into-branches wizard (`SplitBranchesWindow` + conservation ledger + stacked restacking), and the client polish pack (mergetool/difftool/partial stash/patches/templates/diff search/AI draft) — per feature the flow, keyboard map, empty/loading/error states, the one signature accent, the delight moment, and the five-theme reading; conforms to DesignSystem.md + SurfaceDesigns.md, with a gated new-strings inventory in its Appendix A). **`ControlCenterDesign.md`** (Lane E Part 1 — the Phase-2 swarm command surface; §0 records the 2026-07-11 revision: integrated into MainWindow behind a section rail, two layouts (Flight Deck default / Conversation Deck — The Loom retired), Vibe split to its own future app. Original spec: three user-switchable workspace presets persisted like themes, the P2-13 activity bar (attention "pulse" redesigned as a static count badge), the P2-10 merge-queue rail with the stale-cascade delight, the workspace dock + P2-39 pack + P2-44 health strip, the P2-14 TaskPlan approval card + kill-switch spec (quiet octagon, no confirm, with rationale), the P2-11 review cockpit with the item-by-item flagged gate, the P2-29 session board/comparison, the P2-44/45/41 telemetry panels (zero accents), and the ring-family agent micro-badge icon set (§9.3) conforming to E1–E4). **`VibeModeDesign.md`** (Lane E Part 2 — the zero-knowledge founder surface: Soft-Collapse chrome, Build/Pro toggle as a Still view-state swap, the OPS-event→friendly-card translation table, the Vibe dialect rules D-V1/D-V2, the P3-02 triage screen (three verbatim actions as consequence-line option rows, honest in-place disabled state), the P3-04 Publish flow + live-URL card, and the live-preview pane spec). **`ProductAndUX.md`** (Lane I — product & UX depth: §1 the post-C1–C5 delight backlog I-1…I-10, tiered by leverage over shipped seams (stash shelf elevated, conflict forecast, ambient T-19 undo, worktree-first switching, graph-native rebase, rerere surfaced, branch sweep, safety snapshots, pickaxe, away digest); §2 the Nielsen + cognitive-walkthrough usability audit of the shipped flows — clone→commit→push, conflict resolution, interactive rebase — with S1–S3 findings pinned to views/ViewModels (unconfirmed stash drop/file delete, swallowed stash-pop errors, ours/theirs mislabeling incl. the rebase inversion, dead search boxes, terminology drift) plus a positives ledger; §3 the T-18 command-surface completion — coverage contract P-1/P-2, naming grammar P-3…P-8 with the sentence-case renames, the ~50-action inventory with categories and the nine added default gestures, the two-tier global/view-local gesture model, and the five-rung disclosure ladder incl. the Ctrl+/ keyboard map; §4 the T-22 analytics redesign per the dataviz method — validator record (Atelier adjacency + churn Success/Danger deutan collapse, computed) and the mandated secondary encodings M-D1/M-D2, per-chart specs, and implementer deltas §4.8). **`assets/AnalyticsRedesign.html`** (the Part 4 preview: a self-contained five-theme mock of the four redesigned T-22 charts using the DesignSystem Part 1 corrected lanes, with the dataviz validator hook on `body[data-palette]`; a design artifact — the shipping implementation stays LiveChartsCore + `Charts/ChartTheme`). |
| `docs/creative/` | Voice/brand/writing output. **`README.md` is the brand & voice hub — start there** (authority ladder; consolidated 2026-07-12). The Voice & Delight Bible is the law (now incl. **Appendix C** agent naming + **Appendix D** release-notes voice, folded verbatim from the archived LaunchReserve); `Microcopy.md` owns final strings (incl. §6 empty-state strings), `EmptyStates.md` the ES-card layout spec, `MotionPlaybook.md` per-moment motion briefs (read with DesignSystem Part 4), `Onboarding.md` the first-run storyboard, `PolishSpec.md` the live polish punch-list, `ThemeRefinement.md` the color-retune spec (§4.1 superseded by DesignSystem Part 1 — authority note at its top), `Narrative.md` the brand-register market/launch layer of record (honesty contract §0). `LaunchReserve.md` → archived at `docs/obsolete docs/`. |
| `docs/go-to-market/` | Market & GTM. `Mainguard_Master_Market_Document_2026-07.md` is **the strategy hub (the main reference)**; **`GTM_Execution.md` is the execution hub — start there for any fundraising/marketing/sales asset** (indexes all Lane C/F/G/J output + the unified gates-before-dates timeline; consolidated 2026-07-12). `Mainguard_GTM_Plan_2026-07.md` is the historical source the master doc supersedes; `Advisor_Pitch_August_2026.md` stays live as the August-visit engagement brief (complementary to `decks/Mainguard_Deck_Advisor_Board_Update.md`). Lane-F stakeholder/fundraising layer (all brand-register, honesty-contract-bound per `docs/creative/Narrative.md` §0): **`decks/`** — pitch-deck scripts (slide copy + speaker notes, sources inline): `Mainguard_Investor_Deck_Seed.md` (the master seed/pre-seed deck, problem→why-now→wedge→moat→traction→market→model→ask), `Mainguard_Deck_Technical_DeepDive.md` (the safe-to-merge guarantee as the moat, layer by layer with task-ID anchors), `Mainguard_Deck_Advisor_Board_Update.md` (the August advisor working session + recurring report-back template), `Mainguard_Deck_Swarm_Vision.md` (the three-act ten-year story with the swarm-vocabulary discipline stated). **`Mainguard_Business_Plan.md`** — the business plan + financial-model narrative: the story around the Cloud Vibe Companion §5 numbers (unit economics, the BYOK-vs-cloud revenue logic, the pass-through/ARR caveat, the path to the $50+/seat Team tier), adds no new figures. **`Mainguard_Grant_Applications_NL.md`** — application drafts for the Twente/NL subsidy stack (WBSO project descriptions with real task-ID bottlenecks, the VFF early-phase plan, the MIT April-2027 feasibility proposal, the funding calendar); 2026 parameters, re-verify before filing. **`Mainguard_Defensibility_Memo.md`** — the diligence-grade moat argument (five layers with retrofit-cost + erosion scenarios/tripwires, the non-moats conceded, the erosion dashboard). **`Mainguard_Objection_Handling.md`** — concede→fact→line answers to the standing objections ("just use GitHub's merge queue," "Conductor already exists," "agents will get good enough," + nine more), investor/buyer register; HN phrasings stay in `docs/creative/Narrative.md` §5.4. Lane-J paid-acquisition & B2B-sales layer (brand-register, honesty-contract-bound): **`sales/`** — `Mainguard_Paid_Media_Plan.md` (the per-channel paid-media plan for a developer/B2B tool — fit·targeting·budget-share·drafted-creative·measurement for dev newsletters, YouTube/podcast sponsorships, Google Search competitor+intent keywords, Reddit, HN (organic-only, with the honest "HN sells no ads" call), X boosts, LinkedIn (deferred by the do-not-sell-to-Priya rule), Dev.to/Stack Overflow, and OSS/conference sponsorship; a phased pre-launch→launch→scale budget with per-phase kill criteria; and the pixel-free funnel/CAC/attribution model built on self-reported source + landing paths), `Mainguard_Company_Sourcing_Playbook.md` (the ICP sharpened into three findable segments — agent-running teams, Windows-first shops, regulated orgs (nurture-only until governance ships) — with an A/B/C/R/S scoring model and six sourcing plays: Twente/Enschede-map mechanics, LinkedIn Sales Navigator recipes, GitHub org signals (CLAUDE.md/AGENTS.md/copilot-instructions/agent-bot PRs), job-posting queries, communities, and inbound enrichment, all feeding the 40-candidate list), `Mainguard_Outreach_And_Pitch_Kit.md` (the staged design-partner→paid-pilot→Team sales motion, per-persona pitches (EM / compliance / founder / champion), honest two-touch cold-email + LinkedIn-DM sequences (Dutch for Tier 1/2), the discovery→demo→pilot→close flow with the 6-month Track-B pilot structure, pricing/negotiation talking points, and the sales objection table extending Narrative.md §5.4). Lane-G organic marketing/content/launch layer (brand-register, honesty-contract-bound; copy of record for the Show HN body + founder story stays `docs/creative/Narrative.md` §5): **`marketing/`** — `LaunchCampaignPlan.md` (the organic campaign layer on the locked two-act launch: the P2-12 external-PR-intake wedge worded as the act-two "keep your agents" hook with wedge-discipline gates, the per-channel job/asset/cadence table, the L1/L2 sequencing skeleton with sequencing rules that outrank dates, message-discipline checklist, metrics + tripwires), `ContentCalendar.md` (the editorial calendar keyed to L1/L2 plus fully drafted pieces: honest comparison pages vs GitKraken / Fork / Conductor (each opening with the concession, closing with the Narrative §2 sentence; the Conductor page gated present-tense-only), the shipped-code engineering essay "A 60fps commit graph without a chart library" (real types: `CommitGraphRouter`/`GraphFringeState`/`CommitGraphCanvas`), the [Horizon]-tense design essay "The merge queue that re-verifies", and the outlined backlog (index.lock, WSL2 sandbox, AI-review fatigue, pricing logic)), `SocialLaunchReserve.md` (launch-day operations: mirrored Show HN act-one body + the expanded 12-question first-hour FAQ as paste-ready replies, the full act-two Show HN body gated "ships only when true", X/Bluesky/LinkedIn thread series for both acts + the engineering thread, and the About-page founder-story trim), `Manifesto.md` (the standalone thesis essay "Ungoverned AI merges are a time bomb" — the blind-merge enemy framing as prose, five tool-agnostic defusing properties, a self-discounting disclosure, full source ledger; publishes L2 − 3–5 days), `EmailSequences.md` (waitlist nurture → launch sends → time-keyed onboarding drip, designed around the no-login constraint: telemetry-blind copy, essay-driven nurture, gated act-two send), `PressKit.md` (the two-variant press one-pager (Act One / Act Two addendum), fact sheet, outlet angles incl. the Dutch list, pre-cleared quotable lines, the real-capture asset list with the prototype-labeling rule, and the press FAQ), `VideoScripts.md` (the 90-second explainer shot list, the founder-story film script, and the act-two live-demo storyboard narrated in the ControlCenterDesign/VibeModeDesign specs' own state words with the persistent `Design prototype — mock data` labeling rule + the 30-second P2-12 wedge insert). |
| `docs/market-analysis/` | Competitor and market research. |
| `docs/rebrand/` | The Mainguard → **Mainguard** rename. `Mainguard_Rebrand_Plan.md` is the phased plan of record (Phase 0 docs+site landed 2026-07-16; Phases 1–5 cover product strings, the GitHub repo rename, code identifiers, persisted/runtime identifiers with migrations, and the external-services cutover — each phase one green PR). Brand rules themselves live in `PRODUCT.md`/`DESIGN.md`. |
| `docs/members/` | Team structure and intake. |
| `docs/feature-plans/` | Per-task phase-2 feature plans (`P2-01`…`P2-22`): scope, edits, acceptance criteria, test matrix. Subordinate to the master implementation doc. |
| `docs/review/` | Human review guides — what to review, how, and what "correct" looks like. |
| `docs/obsolete docs/` | Superseded docs, kept in full for history: feature plans, feature-plan triage, and `LaunchReserve.md` (archived 2026-07-12; supersession map in its header — §4/§6 live on as Voice Bible Appendices C/D, launch copy in `Narrative.md`). |

**README.md** (repo root) is the product overview (current vs. planned features). **CONTRIBUTING.md** (repo root) is the contributor on-ramp — build/test/run, where code goes, the five hard review rules, testing conventions — and defers to this file wherever they could disagree.

If you change how the app actually works, update **README.md**; if you change the plan, update the relevant `docs/` file and keep it in the correct folder above — don't let them drift.

## Tech Stack

- **.NET 10** (SDK pinned to `10.0.301` via `global.json`, `latestPatch` roll-forward — later patches of the same `10.0.3xx` feature band are accepted, a different feature band is not). C# with `Nullable` enabled everywhere.
- **UI:** Avalonia 11.1.3, Fluent theme, `AvaloniaEdit` (text/diff), `LiveChartsCore` (analytics), compiled bindings on by default.
- **MVVM:** `CommunityToolkit.Mvvm` — use `[ObservableProperty]` and `[RelayCommand]`, not hand-written `INotifyPropertyChanged`.
- **Git engine:** `LibGit2Sharp` 0.30.0 (native libgit2 handles — see the handle rule below).
- **Persistence:** SQLite via EF Core (`Microsoft.EntityFrameworkCore.Sqlite`), migrations applied on startup.
- **Secrets/keys:** `.env` via `DotNetEnv`; OS keyring / `AspNetCore.DataProtection` via `Security/SecureKeyring.cs` (its parameterless ctor resolves storage via `MainguardPaths.DataRoot()` — resolving it with the default `GetFolderPath` is what crash-looped `mainguardd`).
- **Tests:** xUnit + `coverlet`.

## Solution Layout

`Mainguard.slnx` (not a `.sln`). Two edition heads over one shared shell library — you run a
**head**, never the shell. Each project's full description lives in its repo-map file.

| project | role | detail |
|---|---|---|
| **`Mainguard.Git`** (2a) | git engine + persistence base, all editions. A clean leaf: LibGit2Sharp / EF Core / DiffPlex, **no** Docker.DotNet / gRPC / agent dependency. Put new pure git logic here. | [`repo-map/mainguard-git.md`](docs/repo-map/mainguard-git.md) |
| **`Mainguard.Agents`** (2b) | the agent platform built ON `Mainguard.Git`: orchestration, bootstrap/OOBE, adapters, sandbox/container control (Docker.DotNet), terminal/PTY. | [`repo-map/mainguard-agents.md`](docs/repo-map/mainguard-agents.md) |
| **`Mainguard.UI`** (2c) | edition-agnostic design-system base: the five `Themes/*.axaml`, the edition seams (`IEditionManifest`/`IAgentPlatformSurface`), `ViewModelBase`, `ViewLocator`. | [`repo-map/mainguard-ui.md`](docs/repo-map/mainguard-ui.md) |
| **`Mainguard.Agents.UI`** (2e) | Pro-only Views/ViewModels (Control Center / Coordinator / Review) + the daemon client. | [`repo-map/mainguard-app-shell.md`](docs/repo-map/mainguard-app-shell.md) |
| **`Mainguard.Protos`** | proto-first gRPC contract (`Grpc.Tools` codegen), package `mainguard.v1`. | [`repo-map/mainguard-protos.md`](docs/repo-map/mainguard-protos.md) |
| **`Mainguard.Server`** | the headless daemon: ASP.NET Core gRPC host, loopback-only, session-token auth, secret-mask logging. | [`repo-map/mainguard-server.md`](docs/repo-map/mainguard-server.md) |
| **`Mainguard.App.Shell`** (2f/2g) | the edition-agnostic shell **library** (`OutputType` Library) — MainWindow + the git surfaces. **No entry point.** | [`repo-map/mainguard-app-shell.md`](docs/repo-map/mainguard-app-shell.md) |
| **`Mainguard.Client.App`** (2f) | the free **Git-client exe head**; references the shell only, so its closure excludes the agent platform. | [`repo-map/mainguard-app-shell.md`](docs/repo-map/mainguard-app-shell.md) |
| **`Mainguard.Pro.App`** (2f) | the **Pro exe head** and packaging head: shell + Agents.UI + the Mainguard OS payload + OOBE. | [`repo-map/mainguard-app-shell.md`](docs/repo-map/mainguard-app-shell.md) |
| **`installer/Mainguard.Installer`** | the OOBE orchestration driver (diagnostics → state machine → one elevated relaunch). | [`repo-map/installers.md`](docs/repo-map/installers.md) |
| **`installer/Mainguard.Installer.Elevated`** | the tiny elevated helper — the **only** elevated component, doing exactly two privileged actions. | [`repo-map/installers.md`](docs/repo-map/installers.md) |
| **`installer/Mainguard.Uninstall`** | the thin clean-uninstall entry point. | [`repo-map/installers.md`](docs/repo-map/installers.md) |
| **`Mainguard.Tests`** | xUnit for Git + App + client-side daemon pieces; hosts the scripted-agent + daemon-transport test tooling. | [`repo-map/tests-and-tooling.md`](docs/repo-map/tests-and-tooling.md) |
| **`Mainguard.Server.Tests`** | the daemon in-proc test tier (`WebApplicationFactory<Program>` + `Grpc.Net.Client`) and the shared Phase-2 fixtures. | [`repo-map/tests-and-tooling.md`](docs/repo-map/tests-and-tooling.md) |

Not in the solution (scratch/experiments, don't rely on them): `Mainguard.StyleConsole`,
`Mainguard.StyleTests`, `Mainguard.AvaloniaTests`.

## Repository Map — Where Things Live

**The per-file index lives in [`docs/repo-map/`](docs/repo-map/README.md).** It is split by project
so you read only the part you are touching — open
[`docs/repo-map/README.md`](docs/repo-map/README.md) and use its routing table.

**Keep it current: whenever you add, move, rename, or delete a file, update its entry in the
matching `docs/repo-map/` file in the same change.** A new file without a map entry is an
incomplete change.

| Working on… | Open |
|---|---|
| `Mainguard.Git/` | [`mainguard-git.md`](docs/repo-map/mainguard-git.md) |
| `Mainguard.Agents/` | [`mainguard-agents.md`](docs/repo-map/mainguard-agents.md) |
| `Mainguard.Protos/` | [`mainguard-protos.md`](docs/repo-map/mainguard-protos.md) |
| `Mainguard.Server/` | [`mainguard-server.md`](docs/repo-map/mainguard-server.md) |
| `Mainguard.UI/` | [`mainguard-ui.md`](docs/repo-map/mainguard-ui.md) |
| `Mainguard.App.Shell/`, `Mainguard.Agents.UI/`, the two exe heads | [`mainguard-app-shell.md`](docs/repo-map/mainguard-app-shell.md) |
| `Mainguard.Tests/`, `Mainguard.Server.Tests/`, harnesses | [`tests-and-tooling.md`](docs/repo-map/tests-and-tooling.md) |
| `installer/` | [`installers.md`](docs/repo-map/installers.md) |
| `images/`, `site/` | [`images-and-site.md`](docs/repo-map/images-and-site.md) |

**The `Mainguard.Git` vs `Mainguard.Agents` boundary** — the pair most often confused, and the rule
for where new code goes — is tabulated in
[`docs/repo-map/README.md`](docs/repo-map/README.md#by-directory--the-gitagents-boundary).

## Build, Test, Run

Run from the repo root:

```bash
dotnet restore
dotnet build                    # builds the whole solution — do this after any change
dotnet test                     # runs Mainguard.Tests (xUnit)
dotnet run --project Mainguard.Pro.App   # launch the app (Pro head; use Mainguard.Client.App for the Git-client head)
```

**Always run `dotnet build` after making changes**, and `dotnet test` when you touch Core.

### Developing on macOS (the macos-host substrate)

The whole platform runs on a Mac — daemon natively (`osx-arm64`), sandboxes through whichever
Docker engine the machine has (Docker Desktop / OrbStack / Colima; `DockerEndpointResolver`
finds it). See `docs/phase-2/Mainguard_Substrate_MacHost.md` + ADR-008. Mac-specific working
knowledge:

- **Never spawn a copied apphost.** Current macOS pins an executable name to its first-run
  location and SIGKILLs a same-named apphost anywhere else ("died of signal 9"); re-signing does
  not help. Run copied heads through the muxer — `dotnet <app>.dll` — as
  `Mainguard.Tests/TestTools/SelfInvocation.cs` and `MacDaemonController` do.
- **`/var` is a symlink to `/private/var`.** Host git canonicalizes paths; a jail has no such
  symlink. Test fixtures that cross either boundary start from the canonical temp root
  (`TempRepoFixture.CanonicalTempRoot`, `Server.Tests/Fixtures/CanonicalTemp`).
- **Lockfiles are RID-stable**: the daemon-graph projects declare
  `<RuntimeIdentifiers>linux-x64;win-x64;osx-arm64</RuntimeIdentifiers>`, so every restore on any
  platform evaluates identically and a lockfile diff means a real dependency change.
- Jail images build **native arm64** (TARGETARCH-parametrized, per-arch pins); libvterm builds as
  a `.dylib` via the same pinned `build/libvterm/build.sh`.
- Platform traits: `UnixOnlyFact` (Linux + macOS) vs `LinuxOnlyFact` (genuinely Linux-bound) vs
  `MacOnlyFact` — a "not Windows" assumption no longer implies Linux.

### EF Core migrations

`dotnet-ef` is a local tool (`dotnet-tools.json`). The DB is created/migrated automatically on app startup (`App.axaml.cs`). When you change entities in `AppDbContext`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project Mainguard.Git
```

Commit the generated migration + snapshot together. Never hand-edit an applied migration.

## Conventions

- **LibGit2Sharp handles:** always go through `IGitService.ExecuteWithRepo(...)`. It opens/disposes the native `Repository` handle deterministically. Do not hold long-lived `Repository` instances or new one up ad hoc — leaked native handles cause `.git/index.lock` collisions, which is exactly the class of bug this app exists to prevent.
- **Git-directory paths:** never build one as `Path.Combine(repoPath, ".git", …)`. In a linked worktree (`git worktree add` — which the Worktrees window *and* the agent platform's `WorktreeManager` create constantly) `.git` is a **file**, per-worktree state (`HEAD`, `index`, `MERGE_HEAD`, `rebase-merge/`, `rebase-apply/`) lives under `<main>/.git/worktrees/<name>/`, and shared `refs/`/`objects/` live in the common dir. Use `GitService.GitDirPath(repoPath, …)` / `ResolveGitDir` / `ResolveCommonGitDir`, which resolve exactly as git does. Combining against the `.git` *file* yields a path that can never exist, so every state check silently answers "no" — forever. (`Mainguard.Agents`' `GitMutationGuard.ResolveGitDir` is the daemon-side equivalent and is already correct.)
- **MVVM:** ViewModels derive from `ViewModelBase`; expose state with `[ObservableProperty]`, actions with `[RelayCommand]` (async commands as `...Async`). Keep git/IO work in Core services, off the UI thread; marshal back with `Dispatcher.UIThread` when updating bound state.
- **Views:** one `.axaml` + `.axaml.cs` per ViewModel, resolved via `ViewLocator`. Prefer compiled bindings (`x:DataType`).
- **DI:** there is currently **no DI container** — `App` exposes a static `Settings`, and `MainWindowViewModel` is instantiated directly. Follow the existing pattern; if you introduce a container, do it deliberately and update this file.
- **Nullability:** `<Nullable>enable</Nullable>` is on — don't suppress warnings to make something compile; fix the nullability.
- **Tests:** xUnit `[Fact]`/`[Theory]`, naming `Method_ShouldExpectedBehavior_Condition` (see `GitServicesTests.cs`). Tests that touch a real repo create a temp repo and clean it up in `Dispose`.

## UI / Design System

Mainguard ships **one design system with switchable color themes**. The shape language, spacing, typography, and component classes are fixed; only the color palette changes per theme. **Midnight Loom** (layered charcoal + violet accent) is the default; **Daylight Loom** (light), **Command Deck**, **Atelier**, and **Loom Aurora** ship alongside it. The user switches themes via **File → Theme**; the choice persists in `UserPreferences.Theme`.

**Design & voice references.** [`DESIGN.md`](DESIGN.md) is the full design system (colors as roles,
typography, elevation, components, do's/don'ts); [`PRODUCT.md`](PRODUCT.md) is the register, users,
brand personality, and the five design principles;
[`docs/creative/Mainguard_Voice_And_Delight_Bible.md`](docs/creative/Mainguard_Voice_And_Delight_Bible.md)
is the durable anchor for **voice, microcopy, motion & delight, and naming** — numbered, citable
rules (`V-#`/`E-#`/`C-#`/`T-#`/`TT-#`/`ES-#`/`M-#`/`N-#`) that all Wave-2 creative docs (empty
states, microcopy, motion, onboarding, launch copy) conform to. Consult it before writing any
user-facing string, confirmation, toast, or empty state.
[`docs/creative/MotionPlaybook.md`](docs/creative/MotionPlaybook.md) is the Wave-2 **motion** doc —
per-signature-moment easing/timing briefs (commit-graph, selection rail, hover, overlays, toast,
clone progress, analytics reveal, drag-to-rebase, theme switch, merge/verification success, signing
badge) each naming the exact property·duration·easing·token·Avalonia mechanism, plus a global
timing/easing reference table and a "never animate" list, all inside the 120–150ms opacity/brush
budget and citing Bible `M-1…M-7`; consult it before adding or tuning any animation, `Transition`,
or transition timing. [`docs/creative/Microcopy.md`](docs/creative/Microcopy.md) is the Wave-2
**microcopy string inventory** — the highest-stakes user-facing text (the typed
`Mainguard.Git/Exceptions/` errors + `.git/index.lock` recovery rewritten kindly, destructive-action
confirmations, toasts, and tooltips) rewritten into the Bible's voice as a
surface·trigger·raw·rewritten·rule·token table, ending with a microcopy authoring do/don't
checklist; use its exact strings when wiring these surfaces.
[`docs/creative/EmptyStates.md`](docs/creative/EmptyStates.md) is the Wave-2 **empty/zero/first-run
spec** for every surface in the Repository Map — each state tagged `empty-yet` / `not-connected` /
`all-clear` / `loading`, with headline·body·primary-action·motion and cited Bible rules, ending in a
single reusable ES-card component pattern.
[`docs/creative/Onboarding.md`](docs/creative/Onboarding.md) is the Wave-2 **first-run storyboard
and 60-second "aha"** — the very first launch stepped from the `No repository open` empty state
through clone (`CloneDashboardView`), the workspace painting (`RepoDashboardView` →
`CommitGraphCanvas`/`StagingPanelView`/`CommitTimelineView`), the first clean commit, and a theme
switch — as a surface·copy·motion·aha table keyed to the Bible rules, plus the not-over-onboarding
principles (no forced tour, progressive disclosure, keyboard-first); it sequences the EmptyStates
spec rather than re-specifying it. `LaunchReserve.md` (the first-generation brand-register launch
reserve) is **archived** at
[`docs/obsolete docs/LaunchReserve.md`](docs/obsolete%20docs/LaunchReserve.md) as of 2026-07-12 —
its agent-naming options and release-notes voice guide were folded verbatim into the Bible as
**Appendices C and D**, and its launch copy is superseded by `Narrative.md` and
`docs/go-to-market/marketing/SocialLaunchReserve.md`.
[`docs/creative/Narrative.md`](docs/creative/Narrative.md) is the **brand-register market & launch
narrative layer** above LaunchReserve — the evidence-backed competitor teardown
(GitKraken/Fork/Tower/Sublime Merge/Conductor/the orchestrator field/MergeLoom, cited to
`docs/market-analysis/` and `docs/go-to-market/`), the enemy framing ("the blind merge") and locked
positioning registers, the pricing logic behind the locked tier table, the two-act launch narrative
(final Show HN drafts, first-hour comment kit, founder story), and a proposed README hero; where its
copy overlaps LaunchReserve §§1–3/5, Narrative.md wins, and every capability claim is true-today or
marked **[Horizon]**. [`docs/creative/PolishSpec.md`](docs/creative/PolishSpec.md) turns the
`Mainguard_Backlog.md` §B-3 visual/feel-polish backlog into implementable taste specs — per item,
the precise gap plus the fix as concrete design-system moves (token / spacing / radius / motion
`M-#` / microcopy / component class), the real view or control it touches, and verifiable acceptance
criteria, each cited against these rules — ending in a prioritized premium-feel-per-effort
punch-list; consult it before working a polish item.
[`docs/creative/ThemeRefinement.md`](docs/creative/ThemeRefinement.md) is a **color-retune design
spec (proposed values, no live edits)** that audits `Lane1`–`Lane5` and the diff add/remove tokens
for color-vision-deficiency separability and lane/lightness overlap across all five themes (computed
WCAG contrast + deuteranopia-lightness figures), proposes retuned hexes built on a lightness
staircase plus an optional sixth theme (**Loom Meridian**, proposed/not wired, full 32-token
contract + draft `.axaml`), and defines the render-harness + contrast-gate verification any retune
must pass before touching `Themes/*.axaml`; consult it before retuning any lane, diff, or
CVD-sensitive color.

### Theming architecture (read this before touching any color)

- Each theme is one `ResourceDictionary` in **`Mainguard.UI/Themes/<Key>.axaml`** defining the **full token contract** below. `App.axaml` merges `MidnightLoom.axaml` as the startup default.
- **`Mainguard.UI/Theming/ThemeManager.cs`** swaps the merged dictionary at runtime, sets `RequestedThemeVariant` (so built-in Fluent chrome follows light/dark), persists the key, and raises `ThemeChanged`.
- **Color tokens are referenced with `{DynamicResource …}` — never `StaticResource`.** StaticResource is resolved once and will not update on a live theme switch. (`StaticResource` remains correct for theme-independent resources: icons and `FontMono`. `FontUi` is the exception among fonts — it is consumed via `DynamicResource` because the shell overrides it at app level on macOS.)
- **Code-drawn colors** resolve through `Application.Current.TryGetResource(key, app.ActualThemeVariant, …)` with a literal fallback, and long-lived visuals re-resolve on `ThemeManager.ThemeChanged`. `CommitGraphCanvas` is the reference pattern; `DiffViewerView`'s margin renderer and `AnalyticsViewModel.ThemeSkColor` follow it.
- **Adding a theme** = copy `MidnightLoom.axaml`, change values (define *every* token), register it in `ThemeManager.Themes`, add a File → Theme menu item. Nothing else.
- **Adding a token** = add it to **all** files in `Themes/` and to the table below. A token missing from one theme is a runtime bug the compiler cannot catch.

### The golden rule: no raw colors

**Never hardcode a hex color (`#RRGGBB`, `"White"`, `"Black"`) in a View or control.** Bind a named token with `DynamicResource`:

```xml
Foreground="{DynamicResource TextPrimary}"    <!-- yes -->
Foreground="{StaticResource TextPrimary}"     <!-- no — won't follow theme switches -->
Foreground="#CCCCCC"                          <!-- no -->
```

### Token contract (defined per theme in `Themes/*.axaml`)

Reference values are Midnight Loom's.

| Token | Purpose | Midnight |
|---|---|---|
| `SurfaceWindow` | window background the floating cards sit on | `#0F1115` |
| `SurfacePanel` | floating panel / sidebar card surface | `#14171C` |
| `SurfaceDeep` | deepest surface: code/diff editor | `#0B0D10` |
| `SurfaceCard` | inputs, raised cards, segment tracks | `#1A1E24` |
| `SurfaceHover` | hover / neutral selection | `#252B34` |
| `SurfaceHoverGhost` | `SurfaceHover` at 0 alpha — rest background for **ghost** buttons (transparent-looking, hover to `SurfaceHover`) so the fade never flashes white; see Depth & motion | `#00252B34` |
| `ButtonBg` | neutral button fill | `#1E232B` |
| `BorderHairline` | 1px borders, dividers | `#262B33` |
| `TextPrimary` / `TextMuted` | body & titles / metadata, hints | `#E6E9EF` / `#8A93A6` |
| `OnAccent` | text/icons on Accent, Success, Danger fills | `#0B0D10` |
| `AccentBrush` / `AccentHover` | signature accent, links, current branch / its hover | `#8B8BF5` / `#A5A5F8` |
| `AccentSelection` | translucent accent tint for selected rows/chips | `#268B8BF5` |
| `SuccessBrush` / `SuccessHover` | success, added | `#42B968` / `#5BCB7F` |
| `DangerBrush` / `DangerHover` | destructive, removed | `#F87171` / `#FA8C8C` |
| `WarningBrush` | warnings | `#E3B341` |
| `InfoBrush` | informational accent (T-30 Info-severity findings) | `#58A6FF` |
| `Lane1`–`Lane5` | commit-graph lanes (decoupled from semantics) | violet · rose · teal · amber · sky |
| `DiffAddedBg` / `DiffRemovedBg` | diff line backgrounds | `#11271B` / `#33191E` |
| `DiffAddedEmphasis` / `DiffRemovedEmphasis` | intra-line (word-level) emphasis over an added/removed line (T-13) | `#6642B968` / `#66F87171` |
| `DiffWhitespaceMarker` | trailing-whitespace tint (T-13) | `#55E3B341` |

Semantics rule: use tokens **by meaning, not by hue** — the same view must look right in all five themes. Never assume the accent is violet or the background is dark (Daylight Loom is light).

### Shape system — nothing is a bare rectangle

- **Corner radius scale:** `6` buttons/segments/list rows · `8` inputs, small cards, banners · `12` floating panel cards & overlay dialogs · `999` pills, chips, icon-button hovers, toasts. No other radii.
- **Floating panels:** workspace panes are rounded cards (`Border Classes="Card"` → `SurfacePanel`, hairline, radius 12) floating on `SurfaceWindow`, separated by **transparent 8px `GridSplitter` gutters** — never border-fused grid cells. Panels *inside* a card use `Transparent` backgrounds (the card provides the surface); the diff/editor card overrides its background to `SurfaceDeep`.
- **Pills & chips:** the title-bar toolbar's Branch/Sync/Repository dropdown buttons are `Button.Toolbar` (they used to be inline-styled `Classes="Pill"`); ref/branch chips are radius-999 borders on `AccentSelection` with `AccentBrush` text; toasts are radius-999 pills with `OnAccent` text.
- **Selection:** selected rows get `AccentSelection` background plus a 3px rounded `AccentBrush` rail on the left edge (reserve the rail's column so layout doesn't shift — see the sidebar repo row).
- **Focus:** text inputs are radius 8 and get an `AccentBrush` border on `:focus` (global style — don't redefine per view).

### Component classes (defined once in `App.axaml` — pick by role, never inline the look)

| Class | Use for |
|---|---|
| `Button.Primary` | neutral/default actions (`ButtonBg` fill, hairline border) |
| `Button.Accent` | the **one** emphasized CTA per view (`AccentBrush` fill, `OnAccent` text) |
| `Button.Success` | positive/confirming actions (`SuccessBrush` fill, `OnAccent` text) |
| `Button.Danger` | destructive actions that ARE their surface's primary action — chiefly the confirmation step (`DangerBrush` fill, `OnAccent` text) |
| `Button.DangerQuiet` | destructive actions that sit **beside** the view's one accent CTA and must not become a second one — `Button.Secondary`'s unfilled shape with `DangerBrush` text/hairline, so it reads destructive by hue rather than by mass (the merge-queue rail's per-row Discard) |
| `Button.Secondary` | cancel/dismiss (transparent, muted, hairline) |
| `Button.IconButton` | toolbar/inline icon actions — circular hover, padding 6 |
| `Button.Pill` | capsule-shaped buttons |
| `Button.Toolbar` | the title-bar toolbar's Branch/Sync/Repository dropdown buttons — flat/no border/fill at rest, `SurfaceHoverGhost`/`SurfaceHover` hover/press wash, `CornerRadius=6`, `:disabled` dims via opacity rather than filling (right after `Button.Pill` in `DesignSystem.axaml`; replaced the old inline-styled `Classes="Pill"` on those buttons) |
| `Border.SegmentTrack` + `Button.Segment`(+`.Active`) | segmented switches (Commit/Shelf) — never underline tabs |
| `Border.Card` | floating panel card |
| `TextBlock.Mono` | SHAs, code, anything fixed-width (uses `FontMono`) |
| `CheckBox` / `CheckBox.FileRow` | auto-scaled 0.85 / 0.65 — don't inline `RenderTransform` |
| `PathIcon.Chevron`(+`.expanded`), `PathIcon.spinning` | shared chevron swap and spinner |

Rules: at most one `Accent` per view; anything destructive is `Danger` (no ad-hoc reds); cancels are `Secondary`; don't set `Background`/`Foreground` on a classed button (a muted `Foreground` on `Secondary` is the one tolerated exception, e.g. the stash Drop button).

### Typography

`FontUi` (Inter → Segoe UI fallback chain; on macOS the shell overrides it at app level to the system SF Pro face — `.AppleSystemUIFont`, the hidden CoreText family, since "SF Pro Text" is not an installed family and Skia would silently substitute Helvetica — falling back to Inter) is applied to every `Window` globally via `DynamicResource`. `FontMono` is for SHAs, code, and diff text — use `TextBlock.Mono` or `FontFamily="{StaticResource FontMono}"`. Font sizes: `10–11` metadata/chips, `12–13` body/controls, `14` emphasis, `16–18` titles, `24` hero. Spacing scale: `4 / 5 / 8 / 10 / 15 / 20`.

### Icons

Shared `StreamGeometry` resources in `App.axaml`, rendered with `<PathIcon Data="{StaticResource SomeIcon}"/>` (icons are theme-independent — `StaticResource` is correct here). Sizes: **14×14** toolbar/inline, **10–12** chevrons/adornments, **18** nav, **48–64** empty-state art. Add new icons to `App.axaml`; never paste raw path data inline. Muted icon actions use `Foreground="{DynamicResource TextMuted}"`.

### Depth & motion

- Button hover backgrounds fade via a global 130ms `BrushTransition` — free on every button; don't add per-view hover animations.
- **Ghost buttons must rest on `SurfaceHoverGhost`, never `Background="Transparent"`.** The `BrushTransition` lerps color channels in straight (non-premultiplied) RGBA, and the `Transparent` keyword is `#00FFFFFF` (**white**, 0 alpha) — fading transparent→`SurfaceHover` ramps alpha while the RGB is still white, so the hover flashes white. `SurfaceHoverGhost` is `SurfaceHover` at 0 alpha, so only alpha changes across the fade (no color shift). Buttons with an *opaque* rest fill (`.Primary`/`.Accent`/`.Success`/`.Danger`) are unaffected. This is why `.IconButton`/`.Secondary`/`.Segment`/`.WindowButton` and inline ghost buttons set `Background="{DynamicResource SurfaceHoverGhost}"`.
- Overlays (command palette, confirmations): full-bleed scrim `#C0000000`, centered radius-12 card on `SurfacePanel` with hairline border and a soft `BoxShadow` (`0 10 30 0 #40000000`-family literals are fine).
- The commit graph draws with **round line caps** (`PenLineCap.Round`).
- Keep motion subtle: 120–150ms, opacity/brush only. No layout-affecting animations.

### Allowed literal-color exceptions

Semi-transparent black **scrims/shadows** (`#C0000000`, `#40000000`, `#80000000`, …); the repo icon **color-picker swatches** in `MainWindow`/`MainWindowViewModel` (those literals *are* the user-selectable colors, plus the default-dot `TargetNullValue`); **fallback literals** in code-behind theme-brush resolvers; and the legacy conflict-block tints in `ConflictResolverWindowViewModel` (that resolver is replaced wholesale by task T-04 — don't invest in it).

### Before you finish a UI change

Skim sibling views for the same element and match them. If you catch yourself typing a hex value, `StaticResource` on a color, a raw path geometry, an off-scale radius/padding, or a one-off tab/hover style — stop and use (or add) the token/class instead. New tokens go in **every** `Themes/*.axaml` file and the table above; new classes go in `App.axaml` and the class table. Verify with `dotnet build`, and sanity-check your change against both Midnight Loom and Daylight Loom mentally: if it assumes "dark", it's wrong.

## Git Hygiene

- **Line endings are normalized to LF** in-repo via `.gitattributes`. Don't fight it or re-commit whole files as "modified" due to CRLF. Windows-only scripts (`.bat`/`.cmd`/`.ps1`) stay CRLF by rule.
- **Never commit:** `.env`, `*.db`/SQLite/WAL files, `bin/`/`obj/`, IDE folders, or agent session files (`.agents/`, `.antigravitycli/`, `.session_map.json`, `.cortex_plan.md`) — all already in `.gitignore`.
- Secrets live in `.env` (see `.env.example`, e.g. `GITHUB_CLIENT_ID`). Never hardcode credentials or paste them into committed files/docs.
- Commit messages follow the existing `type: summary` style (`feat:`, `fix:`, `ui:`, `docs:`).

### Branching & commits (mandatory)

- **No direct pushes to `main`.** `main` is protected. Every change lands via a Pull Request.
- **One branch per feature/fix.** Branch off the latest `main` (e.g. `feat/agent-executor`, `fix/index-lock`), open a PR, get it reviewed, and merge only when complete and green.
- **Agents commit and push to their OWN branch; agents never merge.** An agent may stage, commit, push its feature branch and open a PR. What an agent must never do is **merge**, force-push, or push directly to `main` or `phase2`. Merging is the human's decision and the review it implies is the point. (This bullet previously read "agents must not commit or push", which was unworkable: it made durable incremental work impossible and was routinely ignored. The real invariant was always *no unreviewed merge*, so that is what it now says.)
- **Commit messages** follow the `type: summary` convention with a body explaining *what changed and why*, not just what.

### Commit early, commit often — work that is not pushed does not exist

**Commit at every meaningful sub-step, and push each one. Not just the finished product.** A reasonable rhythm is a commit when: the plumbing compiles even with logic stubbed; a design decision is made and wired; each test or small group of tests goes green; docs and `docs/repo-map/` are updated. Push the branch on the **first** commit, and after each one after that.

**Why this is mandatory rather than advice.** An entire afternoon of two agents' work was lost in one incident: hours of correct, tested changes existed only as uncommitted files, and the machine died. Nothing was recoverable — no commits, no dangling objects, no stash. A local commit dies with the machine; a **pushed** commit cannot. The cost of an extra commit is nothing; the cost of not having one is everything since the last one.

`wip:` subjects are fine and expected for intermediate commits — a branch with eight honest `wip:` commits is strictly better than a branch with nothing on it. Squash before merge if you want clean history; squashing is cheap and recovering deleted work is not.

**Intermediate commits carry no `Co-Authored-By: Claude` / `Claude-Session:` trailers.** Those belong on the final commit only — the one representing the merged change. Intermediate commits are scaffolding and should carry a plain subject.

### Never build in `/tmp` — it is a RAM disk

**On WSL (and most Linux setups) `/tmp` is a `tmpfs`, which is memory, not disk.** Put worktrees and anything that gets compiled on a real filesystem — `~/mg-work/<task>/` is on `/dev/sdd ext4` here, with hundreds of GB free. Verify with `findmnt -no SOURCE,FSTYPE --target <path>` if unsure.

This is not a tidiness rule. `.wslconfig` caps the VM at 10 GB; a single `dotnet build Mainguard.slnx` writes ~1.7 GB of `bin`/`obj`. Several worktrees built in `/tmp` consumed ~5 GB of RAM before a compiler ran, and the VM was then killed by concurrent test runs — three times, destroying the uncommitted work described above. A `No space left on device` from `/tmp` is a **memory** warning, not a disk one.

**Related: do not run `dotnet test Mainguard.slnx` (the whole solution) locally while other heavy work is in flight.** Use a targeted `--filter`; CI runs the full suite. And never run the `RequiresDocker` suite on a workstation with live agent jails — see the testing restrictions in [`CONTRIBUTING.md`](CONTRIBUTING.md).

### Check `packages.lock.json` before every commit — a WSL build rewrites them

**Building this repo under WSL silently adds a `net10.0/linux-x64` target to three committed lockfiles** (`Mainguard.Agents`, `Mainguard.Git`, `Mainguard.Protos`). No `PackageReference` changed; the restore simply resolved for a RID the committed files were never generated with. Verified: the committed lockfiles contain **zero** `linux-x64` entries, so every WSL build produces this diff.

CI restores with `--locked-mode`, so this either fails somebody's build or gets committed and quietly widens the supply-chain pin — dressed as an intentional dependency change, in a file nobody re-reads.

**So: `git status` before staging, and revert any `packages.lock.json` you did not mean to change.** `git checkout -- '**/packages.lock.json'` is the escape hatch. A lockfile edit is legitimate *only* alongside a deliberate `PackageReference` change, per the restore rule in [`CLAUDE.md`](CLAUDE.md) — and then it should be regenerated with a plain `dotnet restore Mainguard.slnx` and committed with the `.csproj` edit.

### Two long-lived branches: `main` vs `phase2` (added 2026-07-07)

- **`main` — the core Git client** (Master Implementation Document v1, T-01…T-33), now in
  release-hardening mode. Bug fixes, UI polish, tests, and doc corrections for shipped v1
  features branch off `main` and their PRs target `main`, exactly as above.
- **`phase2` — the agent platform.** Everything in
  `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (which lives on that branch: daemon,
  sandboxing, merge queue, review cockpit, audit, installer, market-driven additions) is
  developed there, with its test contract expanded per task in
  `docs/phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md` (the phase-2 companion to the v1 test
  strategy doc) and the orchestration control protocol specified in
  `docs/phase-2/Mainguard_Orchestration_Protocol_Spec.md` (the definitive spec of how the daemon +
  Coordinator control worker agents so only reviewed/verified code reaches the real repo —
  trust model, message catalog, state machines, the A1–A6 hardening decisions, and the
  conformance suite; subordinate to the master doc) and the platform-agnostic substrate
  contract in `docs/phase-2/Mainguard_Environment_Substrate_Contract.md` (ESC / B1 — the umbrella
  `IAgentEnvironment` contract every hosting platform implements: Git-objects-only boundary,
  quarantine remote, hardened sandbox, control-plane-only, no host-destroying teardown; the
  invariants matrix, the reference topology, the portability conformance suite, and the
  cold-start metric framework — with per-platform impls in sibling docs B2
  `docs/phase-2/Mainguard_Substrate_WSL2.md` and future B3…B5; subordinate to the master doc and
  consistent with the orchestration spec). The first per-platform impl is
  `docs/phase-2/Mainguard_Substrate_WSL2.md` (B2 — the `SubstrateId = "wsl2"` realization: the
  `Wsl2AgentEnvironment` facade, `MainguardEnv` VM lifecycle (P2-05), `\\wsl.localhost` UNC
  remote + ext4 bare mirror + 9P-objects-only + quarantine (P2-06), Docker-in-WSL2 hardened
  sandbox (P2-07), Velopack installer/OOBE/teardown/upgrade (P2-21/P2-22), and WAN guardrails
  (P2-25); it realizes ESC-I1…ESC-I9 on WSL2, carries the exhaustive WSL stress matrix and the
  filled cold-start/mount-latency budgets, and runs the ESC §4 `SubstrateConformance` suite plus
  the WSL-specific `RequiresWsl` manual-matrix tests; subordinate to the master doc and to the
  ESC umbrella). The second is `docs/phase-2/Mainguard_Substrate_MacHost.md` (B6 — the
  `SubstrateId = "macos-host"` realization shipped with the phase2 macOS port: the
  `MacHostAgentEnvironment` facade, daemon native on the Mac, engine-agnostic Docker endpoint
  resolution, container-backed CLI/toolchain installs, structural `AllowedMountRoots`, its ESC §4
  results and §5 measurements; the deviation from the deferred B4 "macos-vm" sketch is ADR-008).
  The de-jargoned explainer for the cloud + Vibe roadmap is
  `docs/phase-2/Mainguard_Cloud_Vibe_Companion.md` (a companion for a founder/reader without a
  cloud-infrastructure background: a plain-language concepts glossary — mTLS, OIDC, KMS, tenant
  isolation, per-tenant encryption/crypto-shred, pod eviction, metering, egress, PKCE/loopback
  OAuth, worktree, quarantine remote — a desktop-vs-cloud topology with the trust boundaries drawn,
  the spike → P3-05 → P3-06 → GA de-risking sequence with go/no-go gates, the four open cloud ADRs
  resolved (pod topology, `CloudCredentialProvider`, HTTPS repo sync, metering→billing), and an
  illustrative cost/unit-economics model; explains Wave 3 (P3-01…P3-03, P2-26) + Wave 4 (P3-06/P3-07,
  P2-25, P2-23) without changing them — the master doc wins on any drift). The cross-cutting
  performance contract is `docs/phase-2/Mainguard_Performance_Hotspot_Register.md` (one row per hot path —
  `RepositoryAnalyzer` double-walk, `CommitGraphRouter`/`CommitGraphCanvas` 60fps, libvterm→Skia grid
  throughput + keystroke echo, keep-alive rebase index.lock contention, merge-queue stale cascade,
  gateway lease fairness, control-plane RTT at WAN, cloud pod cold-start — each with a numeric budget,
  failure-at-scale symptom, owning `P2-xx` mitigation, measurement method, and the CI assertion it
  becomes; grounds itself in OPS §2.8/§2.9 and ESC §5, ends with the by-leg CI-assertion list and the
  implied benchmark suite; subordinate to the master doc). The offensive-security companion is
  `docs/phase-2/Mainguard_Orchestration_RedTeam_Plan.md` (RT v1 — a principal red-team of the "agent code is
  safe to PR and merge" thesis: it tries to BREAK each S-1…S-9 / ESC-I1…I9 guarantee — force-push to
  the real remote, forged/replayed `ControlFrame`s incl. the K-from-supervisor-memory scrape, the A5
  merge-gate race + daemon-crash-mid-merge, verification poisoning, stale-cascade/hibernated races,
  event loss across reconnect, VM eviction, secret exfil across every G-13 channel, clock-skew audit
  ordering, a hostile Coordinator, managed-worker input via a hand-crafted client — each ranked
  impact × likelihood, tied to the exact OPS/ESC defense and to a runnable `ScriptedAgentHarness`/
  `SubstrateFixture` test, ending in a go/no-go security checklist + four OPEN DECISIONs RT-D1…RT-D4;
  subordinate to the master doc, consistent with OPS/ESC/B2). The whole-plan program-risk view is
  `docs/phase-2/Mainguard_Risk_Register.md` (a principal-engineer + program-risk red-team spanning DEVELOPMENT /
  SCALE / DEPLOYMENT: 31 ranked risks RR-01…RR-31, one row each with impact × likelihood, early-warning
  signal, mitigation folded into the owning `P2-xx`/`P3-xx`/`T-xx` task, and residual — sorted by
  impact × likelihood; seeds the `.git/index.lock`-under-N-agents existential risk, the swarm-vs-429
  concurrency ceiling, first-party absorption, GPO-locked-Windows provisioning, BYOK zero-exfil,
  the A5 merge race, the Uplift Study efficacy risk, and the unresolved ADRs; raises five program-level
  OPEN DECISIONs OD-R1…OD-R5; cross-references the Lane-C red-team plan for security-specific attacks
  rather than duplicating it; ends with the top-5 watch list + the de-risking spikes/tests that retire
  the highest-ranked risks; subordinate to the master doc, consistent with OPS/ESC/RT). The
  efficacy-evidence spec is `docs/phase-2/Mainguard_Uplift_Study_Spec.md` (P5-01…P5-06 at contract depth — the
  study that proves the pipeline actually raises code-safety uplift: a `Mainguard.Bench.*` harness whose
  arms compose the real P2-06/P2-10/P2-11/P2-35 services, a [STRUCT] held-out oracle (`Arms` never
  references `Oracle`), a planted-hazard→catching-gate map, pure/unit-pinned metrics + stats, a
  pre-registration hash-committed to the P2-15 audit chain, and a CI Quality Bench gate; subordinate to
  the master doc). Task branches
  fork from `phase2`; PRs target `phase2` (`gh pr create --base phase2`).
- Merge `main` into `phase2` regularly so platform work never drifts from client fixes.
  A v1 bug discovered while working on `phase2` is fixed via a `main`-targeted PR, never
  only on `phase2`.
- When the core client is released, `phase2` merges into `main` once, `main` becomes the
  single center again, and this subsection is removed.

## For AI Agents Specifically

- Make the smallest change that satisfies the request; match surrounding style rather than reformatting files.
- **Keep the repo index current.** Whenever you **create, move, rename, or delete a file**, update its entry in the matching [`docs/repo-map/`](docs/repo-map/README.md) file in the same change so the entire repo stays indexed — use that folder’s routing table to find the right one. Add the new file under the right heading with a one-line description of what it holds; remove entries for files you delete. A new file without a map entry is an incomplete change.
- When you add or change UI, follow the **UI / Design System** section: no hardcoded colors, reuse `App.axaml` tokens/styles, and add any new token/icon there (and to its table) rather than inlining it.
- Put business logic in `Mainguard.Git` (git/persistence) or `Mainguard.Agents` (agent platform) behind an interface; keep `Mainguard.App.Shell` (and the exe heads) thin.
- Verify with `dotnet build` (and `dotnet test` for Core changes) before declaring done. Report failures with output — don't paper over them.
- **Do not commit or push.** Make the edits, then hand back a detailed proposed commit message and let the human commit. Never touch `main` directly.
- Don't invent features from the roadmap into the code unless asked; the docs are forward-looking.
