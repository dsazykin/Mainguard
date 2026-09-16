# ADR-0001 — Separate Client / Pro / Cloud by edition composition on one trunk, not by branch

**Status:** Accepted — 2026-07-19.

Repo-wide product-structure decision. Sibling to the client/non-security Lane-H log in
[`docs/phase-2/ADRs.md`](../phase-2/ADRs.md); this `docs/adr/` register holds cross-cutting
architecture/release decisions. This ADR is step 1 of the approved edition plan (see References);
it records the model and changes the release ladder, and it precedes any file moves.

## Context

Mainguard is three products — the free **Client**, **Pro** (the local agent platform), and
**Cloud/Vibe** (the zero-git cloud builder). Today Client and Pro are separated by *branch*:
Client on `main`, Pro (= Client + the in-progress agent platform) on `phase2`. Branches model
**time, not products**, and that mismatch is the cost we keep paying:

- **A recurring merge tax.** The last `main → phase2` sync was 750 files / 17 conflict hunks, and
  it grows with the agent platform's surface area.
- **Double CI**, and **no commit that *is* "just the client."** Client and Pro are the same files
  at different points in time, so a free-client build can only be a build-time subset of a branch
  — and nothing in the code expresses "leave the agent platform out."

The three products are **asymmetric**, so do not build three equal silos:

- **Client and Pro are editions of one shell.** Pro is Client **plus additive** agent surfaces
  (extra rail sections, menu entries, dialogs) around the same core git surfaces — it does not
  restyle the client. Additive differences are exactly what composition handles.
- **Cloud/Vibe is a separate head** over a shared engine (`VibeOrchestrator`), cloud-first — an
  app-boundary problem, not an edition problem. It stays out of the desktop conversation for now.

The seams already exist: invariant **G-18** keeps the UI off Docker/WSL/PTYs (the Pro backend —
`mainguardd` + protos + the MainguardOS payload — is already a separable unit), and
`App.OrchestratorServicesFactory` is already a swappable `Func<OrchestratorServices>`. The single
hard-wired Pro construction is one line — `ControlCenter = new(App.CreateOrchestratorServices())`
in `MainWindowViewModel`. Make *that* the seam and the shell becomes edition-agnostic.

## Decision

1. **One trunk + edition composition + per-edition packaging lanes.** The Client/Pro separation
   lives in **project structure and composition roots** — never in branches and never in `#if`
   flags.

2. **A static `IEditionManifest` / `App.Edition` seam** (mirrors the existing static `App.Settings`
   — no DI container). Chosen once at the composition root, it answers what differs by edition:
   the product name, whether the agent platform is present (`HasAgentPlatform`), the rail sections,
   the first-run flow, and which orchestration services (if any) to construct
   (`CreateControlCenter()` → `OrchestratorServices?`, null under the Client manifest so nothing
   Pro is even instantiated). The rail becomes an `ItemsControl` over `manifest.Sections`; every
   other Pro affordance is an `IsVisible` gate on `HasAgentPlatform`.

3. **Physical separation = assemblies, not copies.** Pro-only Views/ViewModels (Control Center,
   agent workspace, merge queue, review cockpit, terminal, OOBE wizard, adapter/egress settings,
   Vibe VMs) plus the Pro manifest move into a **`Mainguard.Agents.UI`** assembly. Two thin exe
   heads — **`Mainguard.Client.App`** and **`Mainguard.Pro.App`** — compose the shared shell with
   the Client or Pro manifest. The Client binary then **cannot physically contain** Pro code: no
   flag to crack, nothing to leak, download stays small. (The one enabling framework change is a
   multi-assembly `ViewLocator`, resolving a VM's View across the manifest's registered
   assemblies instead of the executing assembly only.)

4. **The `Mainguard.* → Mainguard.*` rename is fused with the assembly split** into a single
   merge-freeze operation. They are the same kind of mechanical churn (project-structure +
   namespace); done apart they pay the rebase tax twice and risk renaming files about to move (or
   moving files about to rename). Rename **straight into** the final split layout
   (`Mainguard.Agents` → `{Mainguard.Git, Mainguard.Agents}`, Pro UI → `Mainguard.Agents.UI`).

5. **The free Client edition ships a dedicated "Clone" first-run** — not the Pro MainguardOS
   provisioning wizard/OOBE. First-run flow is one of the things the manifest selects.

6. **Enforcement is by CI gates, not directory hygiene.** Three gates keep the boundary real the
   way G-11/G-18 already do: a **reference-graph gate** (the Client head's reference closure must
   be free of the agent-platform / daemon / Docker assemblies — a stray `using` becomes a red
   build, not a shipped leak); a **twin render harness** (the shell render harness runs once per
   edition manifest, so "did the client grow a kill switch?" / "did Pro lose the coordinator?"
   become screenshot diffs); and a **manifest-completeness test** (every section/page a manifest
   lists must resolve to a View via the multi-assembly `ViewLocator`).

## Consequences

**Release ladder.** Two Velopack channels, built **from one commit**:

- **`mainguard-client`** — shell + Client manifest. No payload, no daemon. The free, fast install.
- **`mainguard-pro`** — shell + `Mainguard.Agents.UI` + `mainguardd` + the MainguardOS payload + OOBE.

`phase2` is **retired** in favour of trunk-based development once the layout lands; feature
branches only thereafter. Separate download vs. in-binary license unlock stays a business call —
the manifest supports either; recommend separate channels (small free download, agent-platform IP
kept out of it). Cloud/Vibe ships later as its own head (`Mainguard.Vibe.App`) over
`VibeOrchestrator` — a deploy pipeline, not a desktop installer.

**Explicitly deferred — the full client-closure reference gate.** The *complete* gate (parsing the
Client head's published `.deps.json` for the **absence** of the Agents / Protos / Docker / Grpc
assemblies) is a **later deliverable**: it cannot pass until `Mainguard.Agents` is split and a separate
Client head exists. The **first** CI gate (landing now, against today's single-head layout) asserts
only what already holds — the UI does not reference Server/Protos/Docker — and hardens into the
full closure check after the fused split (Decision 4).

**In-flight branches.** The logical seam (Decisions 2, 5, 6-first-gate) needs no freeze and lands
in-place on trunk. The fused rename+split (Decision 4) must land in a real merge freeze; every open
`feat/*` / `feature/P2-*` branch rebases through it. Prefer a quiet, announced window — the
freeze prerequisite (audit PR series merged) is now satisfied.

## References

- [`docs/archive/planning/Product_Editions_And_Structural_Sequencing.md` (archived — shipped)](../archive/planning/Product_Editions_And_Structural_Sequencing.md)
  — the approved edition plan; §6 is the ordered execution steps this ADR is step 1 of, §7 owns the
  rename-vs-split sequencing.
- [`docs/archive/Mainguard_Rebrand_Plan.md` (archived)](../archive/Mainguard_Rebrand_Plan.md) — the phased
  `Mainguard → Mainguard` rename; Phase 3 (code identifiers) is the half fused with the assembly
  split per Decision 4.
