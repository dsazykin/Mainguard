# Product editions & structural sequencing — separating Client / Pro / Cloud

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

**Status:** PLAN (2026-07-18). Not started. Companion to
[`docs/rebrand/Mainguard_Rebrand_Plan.md`](../Mainguard_Rebrand_Plan.md) — the two overlap
in one expensive operation (§7), and this document owns the sequencing between them.

## 0. TL;DR

Mainguard is three products — the **free Git Client**, **Pro** (the local agent platform), and
**Cloud/Vibe** (the zero-git cloud builder). Today they are separated by *branch* (client on
`main`, Pro on `phase2`), which is the wrong axis: branches express time, not products. That is
the source of the merge tax (the recent `main→phase2` sync was 750 files / 17 conflict hunks),
the double CI, and the fact that "the client" and "Pro" can never be built from the same commit.

The fix is **one trunk + edition composition + per-edition packaging lanes** — the separation
lives in project structure and composition roots, never in branches or `#if` flags. The three
products are **not symmetric**: Client and Pro are *editions of one shell*; Cloud is *a separate
app over a shared engine* (already the direction in ControlCenterDesign §0).

**Recommended order (see §7):** cheap rebrand strings first → the **logical edition seam** next
(highest leverage: it makes a free-client build from trunk real, with no file moves) → then fuse
the two expensive structural jobs — the **project rename** (rebrand Phase 3) and the **assembly
split** — into a **single merge-freeze operation** so the churn tax is paid once.

## 1. The problem with the current split

- **Branches model time, not products.** `main` = the shipped client; `phase2` = client + the
  in-progress agent platform. Every client fix must be merged forward; every Pro change that
  touches a shared surface risks conflicting with client work. There is no commit that *is* "just
  the client" and another that *is* "Pro" — they are the same files at different points in time.
- **The merge tax is structural, not incidental.** It recurs on every sync and grows with the
  agent platform's surface area.
- **You cannot package what you cannot compose.** A free-client installer would today have to be
  a *build-time subset* of `phase2` — but nothing in the code expresses "leave the agent platform
  out," so the only lever is a branch, and branches drift.

## 2. The three products are asymmetric

| Product | Relationship | Packaging | Today |
|---|---|---|---|
| **Git Client** (free) | Base edition of the desktop shell | Velopack channel, small, no daemon/VM payload | shipped, on `main` |
| **Pro** | The client shell **+ additive** agent surfaces | Velopack channel: shell + `mainguardd` + MainguardOS payload + OOBE | engines built on `phase2` |
| **Cloud / Vibe** | A **separate app** over the shared `VibeOrchestrator` engine; cloud-first | a deploy pipeline, not a desktop installer | `VibeOrchestrator` engine specced (P2-26); UI split off per ControlCenterDesign §0 |

The critical asymmetry: **Pro is Client plus additions, not a different app.** P2-48 already chose
*one installed experience* (single exe + in-app OOBE + launch routing). So Client↔Pro is an
**edition** problem (composition inside one shell); Cloud is an **app-boundary** problem (a second
head). Design for that shape — do not build three equal silos.

## 3. Why the UI is the interesting part (the owner's concern)

The worry: "the UI is shared between Client and Pro; there are a lot of differences — how do we
separate them?" The load-bearing observation:

> **Almost every Client↔Pro UI difference is *additive*, not *divergent*.** Pro does not restyle
> the client — it adds sections, menu entries, and dialogs around the same core surfaces.

Additive differences are exactly what composition handles cleanly, and the seams already exist:

- **The daemon boundary is already enforced.** Invariant **G-18**: the UI never touches
  Docker/WSL/PTYs — only the daemon's gRPC surface. So the entire Pro *backend* is already a
  separable unit (`Mainguard.Server` + `Mainguard.Protos` + the MainguardOS payload).
- **The composition seam already exists in embryo.** `App.OrchestratorServicesFactory` is a
  swappable `Func<OrchestratorServices>` (production = `DaemonBackedOrchestrator.CreateBundle()`;
  the render harness injects a scripted `MockOrchestrator`). This is the manifest pattern, waiting
  to be generalized.
- **The single hard-wired Pro construction is one line:**
  `public ControlCenterViewModel ControlCenter { get; } = new(App.CreateOrchestratorServices());`
  in `MainWindowViewModel`. Every Pro surface hangs off `ControlCenter`. Make *that* the seam and
  the shell becomes edition-agnostic.

### 3.1 The mechanism — one shell, an edition manifest

Introduce `IEditionManifest` (a static, exactly like `App.Settings` — this respects the no-DI
rule). It answers the questions that differ by edition:

```
interface IEditionManifest
{
    string ProductName { get; }              // "Mainguard" | "Mainguard Pro"
    bool HasAgentPlatform { get; }            // gates every Pro affordance
    OrchestratorServices? CreateControlCenter();   // null for Client
    IReadOnlyList<RailSection> Sections { get; }   // which rail destinations exist
    IReadOnlyList<MenuGroup> FileMenuGroups { get; }
    IReadOnlyList<SettingsPage> SettingsPages { get; }
    FirstRunFlow FirstRun { get; }            // Client → Clone; Pro → OOBE wizard
}
```

`App.Edition` is chosen once at the composition root. Then:

1. **The rail is the one real refactor.** It is the only place the *shape* differs. Today its
   items are hard-coded (`IsRepoSectionActive`, `IsCoordinatorSectionActive`,
   `IsResourcesSectionActive`, the host tabs, the agent list, the kill switch). The host tabs
   (PRs/Issues/Notifications/Releases) are **client** features (T-23…T-33); Coordinator,
   Resources, the live agent list, and the kill switch are **Pro**. Turn the rail into an
   `ItemsControl` over `manifest.Sections` and the shape difference becomes data.
2. **Everything else is `IsVisible` on a `HasAgentPlatform` binding** — the File menu's
   Layout / Agent-prompting / "Stop Mainguard OS on exit" items, the Tools flyout's AI Providers /
   Agent CLIs entries, the Pro Settings pages. No new surfaces, just gates.
3. **`ControlCenter` becomes `App.Edition.CreateControlCenter()`** — null under the Client
   manifest, so nothing Pro is even constructed.

### 3.2 Physical separation = assemblies, not copies

Once the manifest names the boundary, the Pro-only Views + ViewModels move to their own assembly:

- **`Mainguard.Agents.UI`** — `ControlCenterView(Model)`, `AgentWorkspaceView(Model)`,
  `MergeQueueView(Model)`, `ReviewCockpitView(Model)`, `TerminalView(Model)`, the OOBE wizard,
  `BootstrapProgressView(Model)`, `AgentCliSettingsView(Model)`, `EgressAllowlistView(Model)`,
  Vibe VMs, and the **Pro manifest** itself.
- Two thin exe heads: **`Mainguard.Client.App`** (references the shell only) and
  **`Mainguard.Pro.App`** (references `Agents.UI`, hands the shell the Pro manifest).

The client binary then **cannot physically contain** Pro UI — there is no flag to crack and
nothing to leak, and the download stays small. Stays **100% shared, untouched:** the design system
(tokens, five themes, component classes), the git surfaces (graph, staging, diff, resolver,
branch/Sync/Tools nav), host integration, and the git engine underneath.

### 3.3 The one required framework change

`ViewLocator.Build` resolves views with `Type.GetType(name)` — which only searches the executing
assembly. A multi-assembly shell must resolve a VM's View across the **manifest's registered
assemblies** (search `AppDomain` loaded assemblies, or have each manifest contribute an assembly
list). Small, well-understood change; it is the *only* framework-level edit the split needs.

## 4. Target architecture

**One trunk.** After the Alpha integration stabilizes, merge `phase2` down to `main` and retire
`phase2`; feature branches only thereafter. (As of 2026-07-18 the branches are routinely synced,
so the gap is process, not code.)

**Layered projects** (final Mainguard names; see §7 for why the rename rides this move):

```
Mainguard.Git            git engine: IGitService, graph, diffs, models        (all editions)
Mainguard.UI             design system, themes, shared controls, ViewLocator  (all editions)
Mainguard.Agents         orchestration client seams, adapter/OOBE logic       (Pro)
Mainguard.Server/Protos  the daemon + gRPC contract                           (Pro payload)
Mainguard.Agents.UI      Pro-only Views/ViewModels + the Pro edition manifest (Pro)
Mainguard.App.Shell      the edition-agnostic shell (MainWindow, git surfaces)
Mainguard.Client.App     exe: shell + Client manifest                          → client channel
Mainguard.Pro.App        exe: shell + Agents.UI + Pro manifest                 → pro channel
Mainguard.Vibe.App       later: its own head over VibeOrchestrator             → cloud pipeline
```

**Packaging lanes — one artifact per lane, all from one commit:**

- `mainguard-client` — Velopack channel: shell + Client manifest. No payload, no daemon. The
  "free forever, no login" fast install.
- `mainguard-pro` — Velopack channel: shell + Agents.UI + `mainguardd` + the MainguardOS tarball +
  OOBE. (Separate download vs. license-key unlock inside one binary is a **business** call — the
  architecture supports both. Recommend separate channels: keeps the free download small and the
  agent-platform IP out of it.)
- `mainguard-cloud` — a deploy pipeline for the Vibe head, not an installer.

## 5. Enforcement — how the boundary stays real

Directory names and good intentions do not keep code separated; **CI gates** do (the same way
G-11/G-18 are enforced today).

1. **Reference-graph gate.** A test (NetArchTest, or the existing grep style) fails the build if
   the Client head's reference closure contains `Mainguard.Agents(.UI)`, `Mainguard.Protos`, or
   `Docker.DotNet`. A stray `using` becomes a red build, not a shipped leak. **This gate is
   independent of everything else and can land today** against the current layout (it already
   holds for Server/Protos).
2. **Twin render harness.** The shell render harness runs **once per manifest**. "Did the client
   accidentally grow a kill switch?" and "did Pro lose the coordinator?" become screenshot diffs,
   not bug reports. (Builds on the now-mandatory `HarnessHygiene.Teardown` discipline, #186.)
3. **Manifest completeness test.** Every `RailSection`/`SettingsPage` a manifest lists must
   resolve to a View via the multi-assembly ViewLocator — no dangling sections.

## 6. Execution — the ordered steps

Each step is a green PR that leaves the repo shippable. Steps 1–3 are **logical** (single project,
no file moves); step 5 is the **one heavy structural operation**.

1. **ADR + this plan.** Record the edition model as an ADR (it changes the release ladder) and
   land the **reference-graph CI gate** against today's layout. *Small, unblocks nothing, proves
   the invariant holds now.*
2. **`IEditionManifest` + `App.Edition` seam,** in-place in `Mainguard.App.Shell`. Define the Client and
   Pro manifests; move `OrchestratorServicesFactory` under the manifest; route `ControlCenter`
   through `App.Edition.CreateControlCenter()`.
3. **Gate the rail + menus + settings** behind the manifest. Convert the rail to
   `ItemsControl over manifest.Sections`; add `HasAgentPlatform` `IsVisible` gates. **Outcome: a
   runnable free-client edition from the same commit as Pro** — the milestone that ends the
   branch-as-product model in practice, before any file moves.
4. **Multi-assembly `ViewLocator`** (§3.3). Land it while still single-project so the change is
   isolated and testable on its own.
5. **THE STRUCTURAL OPERATION (one merge-freeze window):** rename `Mainguard.* → Mainguard.*`
   **and** split into the §4 assemblies **simultaneously** — going straight from `Mainguard.Agents` to
   `{Mainguard.Git, Mainguard.Agents}` with final names, Pro UI to `Mainguard.Agents.UI`, two thin
   exe heads. This *is* rebrand Phase 3 fused with the physical split (see §7). Update every CI
   grep, reviewer script, and the AGENTS.md map in the same PR.
6. **Two Velopack channels** in CI. P2-48's installer becomes the Pro channel's OOBE; the Client
   channel stays a bare fast install. Add the twin render harness (§5.2).
7. **Vibe/Cloud as its own head** over `VibeOrchestrator` — later, cloud-first.

## 7. Sequencing: rebrand vs. separation — the recommendation

**They are not either/or, and the honest framing is three buckets, not two:**

| Bucket | Work | Cost | When |
|---|---|---|---|
| **Cheap & safe** | Rebrand Phase 1 (product strings) + Phase 2 (repo rename); edition manifest + rail gating (sep. steps 2–3); the CI arch gate | low, in-place | **now / soon**, in any order |
| **The fused heavy op** | Project **rename** (rebrand Phase 3) **＋** assembly **split** (sep. step 5) | high, needs a merge freeze | **one window, together** |
| **Later, gated** | Rebrand Phase 4 (persisted-id migrations); Velopack channels; Vibe head | medium–high | before public alpha / post-split |

**The key insight:** the project rename and the assembly split are *the same kind of operation* —
massive, mechanical, project-structure + namespace churn that both want a merge freeze and both
rebase every in-flight branch. Doing them separately pays that tax **twice** and risks renaming
files you are about to move (or moving files you are about to rename). **Fuse them.**

**So, concretely:**

1. **Rebrand strings first (Phase 1).** Cheap, low-risk, and it makes the manifest/rail code read
   as "Mainguard" throughout instead of mixing names mid-refactor.
2. **The logical edition seam next (steps 2–3).** *Highest leverage single move in the whole
   plan* — it delivers a shippable free client from trunk with zero file moves, and it defines the
   boundary the physical split will follow. Do this **before** the heavy op; you cannot cleanly
   split until the manifest says what goes where.
3. **Repo rename (Phase 2)** whenever — one owner click + a small PR.
4. **Then the fused structural op (step 5 = Phase 3 + split)** in one freeze window. Prereq —
   **now satisfied:** the audit series has merged (memory: #182/#191/#192 complete), so the freeze
   window the rebrand plan was waiting on is available.
5. **Phase 4 migrations + packaging lanes** after the split, before the first public alpha
   installer.

**One-line answer to "rebrand or separation first":** do the *string* rebrand and the *logical*
edition seam first (both cheap, both unblock value), and **merge the expensive halves of each into
a single structural operation** rather than sequencing rename-then-split or split-then-rename.

## 8. Risks & open decisions

- **In-flight branches.** Many `feat/*` and `feature/P2-*` branches are open. Step 5 (and rebrand
  Phase 3) must land in a real freeze or every one of them rebases through the rename+split. Prefer
  a quiet window; announce it. The logical steps 2–4 do **not** need a freeze.
- **`ViewLocator` trimming.** The reflection-based locator is already `[RequiresUnreferencedCode]`;
  the multi-assembly version must keep the Pro assemblies out of the client's trim closure (they
  are simply never referenced by the Client head — which is also what the arch gate asserts).
- **Business decision, not architectural:** Pro as a separate download vs. an in-binary license
  unlock. Recommend separate channels (small free download, IP separation). The manifest supports
  either; only the packaging lane differs.
- **Cloud/Vibe timing.** Its packaging is a *deploy* problem; keep it out of the desktop
  installer conversation entirely until the desktop editions ship.
- **Migrations (rebrand Phase 4)** — `MainguardEnv` distro, `mainguardd`, `%LocalAppData%\Mainguard`,
  registry, Velopack app id — are orthogonal to editions but must precede the first public
  installer; tracked in the rebrand plan, not here.

## 9. Definition of done (the editions work, independent of the rename)

- [ ] `IEditionManifest` + `App.Edition`; Client and Pro manifests exist.
- [ ] Rail is data-driven over `manifest.Sections`; Pro affordances gated on `HasAgentPlatform`.
- [ ] A free-client edition builds and runs from trunk (no Pro types constructed).
- [ ] Multi-assembly `ViewLocator`.
- [ ] Pro UI in `Mainguard.Agents.UI`; two thin exe heads.
- [ ] Reference-graph CI gate green (client closure free of Agents/Protos/Docker).
- [ ] Twin render harness (client + Pro manifests) green.
- [ ] Two Velopack channels produce a small client installer and a full Pro installer **from one
      commit**.
- [ ] `phase2` retired; trunk-based development in effect.
