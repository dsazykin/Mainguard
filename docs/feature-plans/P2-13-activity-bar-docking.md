# P2-13 — Activity Bar & Docking UI — Implementation Plan

**Task ID:** P2-13 · **Milestone:** M7 · **Priority:** P0
**Depends on:** P2-02 (`DaemonClient` connection state, `GatewayService` spend stream),
P2-03 (`TerminalView`).
**Branch:** implement on `feature/P2-13-activity-bar-docking` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated VM/memory/render tests + **screenshot testing and human visual approval required**.
> Ordering, attention derivation, status-to-token mapping, and the 50x open/close memory harness are automated (memory harness is nightly but blocking). The section rail + two layouts are a flagship visual surface: PNGs of the rail with 4 fake agents in **every one of the five themes** + a human pass against ControlCenterDesign §0/§2/§4 (spacing, badge legibility, kill-switch prominence) are required before merge.
>
> **Source of truth:** §P2-13 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.4). v1 UI rules apply unchanged: design tokens via `{DynamicResource}` only,
> component classes over raw colors, five themes, Repository Map current.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-13 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-13** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-13 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Design decisions (binding)** | [`ControlCenterDesign.md`](../design/ControlCenterDesign.md) §0 (revision of record) + §2/§4 -- the control center is **integrated, not a separate window**; see the 'Design revision of record' section added below |

---

## 0. Context — what exists today

The app is a single-window Git client (`MainWindowViewModel` + panels). Agents need a workspace
model: per-agent dock layouts (terminal + agent-diff + staging), an activity bar that shows the
whole swarm at a glance, and disciplined teardown (Dock.Avalonia has a documented floating-window
leak — this task owns the mitigation).

### What you can rely on

| Fact | Where |
|---|---|
| `DaemonClient` connection-state enum (`Connected/Degraded/Down`) + `StreamAgentEvents` | `Mainguard.App.Shell/Services/DaemonClient.cs` (P2-02) |
| `GatewayService.StreamSpend` + `GetSnapshot` (per-agent spend, queue depth) | P2-08 |
| `TerminalViewModel`/`TerminalView` behind `ITerminalView` | P2-03 |
| Diff + staging panels composable per repo path (agent worktree = a repo path) | `DiffViewerViewModel`, `StagingPanelViewModel` |
| MVVM: CommunityToolkit, `ViewLocator` pairing, no DI | `Mainguard.App.Shell/` |
| Design tokens in every `Themes/*.axaml`; classes/icons in `App.axaml` | v1 design system |

New dependency (App): `Dock.Avalonia`. Keep it out of `Mainguard.Agents`.

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.App.Shell/ViewModels/Agents/ActivityBarViewModel.cs`, `AgentCardViewModel.cs`, `ResourceMonitorViewModel.cs` |
| **Create** | `Mainguard.App.Shell/ViewModels/Agents/AgentWorkspaceViewModel.cs` (dock factory: Terminal + agent-diff + staging per agent) |
| **Create** | `Mainguard.App.Shell/Views/Agents/ActivityBarView.axaml(.cs)`, `AgentWorkspaceView.axaml(.cs)` |
| **Create** | `Mainguard.App.Shell/Services/DockLayoutPersistence.cs` (layout save/restore per agent kind) |
| **Create** | `Mainguard.App.Shell/Converters/AgentStatusBrushConverter.cs` (the **one** status→brush mapping) |
| **Create** | `Mainguard.App.Shell/Services/AgentNotificationService.cs` (OS notifications on waiting/blocked transitions) |
| **Edit** | `MainWindowViewModel` / `MainWindow.axaml` (section-rail region + workspace host — **evolve the existing `phase2` control-center prototype's Views/ViewModels; do not create parallel ones**) |
| **Edit** | the prototype's mock services → `DaemonClient`-backed implementations behind the same interfaces (zero View changes — the §0 acceptance) |
| **Edit** | `Themes/*.axaml` ×5 (new status tokens) + `App.axaml` (classes/icons) |
| **Create** | `Mainguard.Tests/AgentStatusBrushTests.cs`, `ActivityBarOrderingTests.cs`, `AttentionDerivationTests.cs`, `DockTeardownMemoryTests.cs`, `ActivityBarRenderTests.cs` (headless PNG) |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (binding)

> **Design revision of record (2026-07-12 — binding; ControlCenterDesign §0 + §2/§4):** the
> control center is **integrated, not a separate window**. `MainWindow` keeps its full v1
> chrome and gains a leftmost **section rail** (collapsible like the repo sidebar): top third =
> Repo viewer / Coordinator (with attention badge) / Resources / the relocated git-host icons;
> bottom two-thirds = the live agent list; **the kill switch at the rail's foot, always visible
> in every section** (P2-14 renders it; the slot ships here). **Two layouts only** — **Flight
> Deck** (default) and **Conversation Deck** — picked in File → Layout, persisted as
> `UserPreferences.WorkspaceLayout`, applying to coordinator surfaces only (the Repo viewer
> never changes shape). **The 2026-07 control-center prototype already on `phase2`
> (Views/ViewModels + mock services) is the reference implementation — start from it, do not
> greenfield.** Its mock services are shaped like the gRPC contract, so P2-02's `DaemonClient`
> swaps in **with zero View changes**; this task's "activity bar" rows below are realized as
> the rail + agent list of that prototype. Where the pre-revision wording below says "activity
> bar", read "section rail" per §0.

- **Workspace:** `Dock.Avalonia` per-agent workspace — Terminal + agent-diff + staging docked
  panes; layout persisted and restored (`UserPreferences.WorkspaceLayout`: Flight Deck |
  Conversation Deck — exactly two).
- **Rail top third (Row 0):** Resource Monitor — VM CPU/RAM sparklines + token-spend counters
  from `GatewayService`; pinned sections incl. **Coordinator** with an `IsAttentionRequired`
  pulse; relocated git-host icons; kill-switch slot at the rail foot.
- **Rail bottom two-thirds (Row 1):** virtualized **LIFO** agent list (newest first).
- **Status micro-badges** via one `AgentStatus → Brush` converter using theme tokens (all five
  themes — new tokens added to every `Themes/*.axaml`).
- **OS notifications** on transitions into waiting/blocked; suppressed when the app is
  foregrounded **on that agent**.
- **Teardown discipline:** `IDisposable` workspace VMs, timers stopped, floating dock windows
  closed (the documented Dock.Avalonia leak), `WeakReferenceMessenger` only (no strong
  event-handler webs).

---

## 3. Implementation steps

1. **Status tokens first:** add `AgentStatus.*` color tokens (Working, Verifying, Verified,
   Stale, AwaitingReview, Conflict, RateLimited, Dead, Paused) to all five theme files +
   semantic classes in `App.axaml`. `AgentStatusBrushConverter` resolves token keys — never
   literal brushes.
2. **Rail/`ActivityBarViewModel`:** (the prototype's rail VM) subscribes `StreamAgentEvents` (agent add/remove/state) and
   `StreamSpend`; Row 1 is an `ObservableCollection<AgentCardViewModel>` inserted at index 0
   (LIFO) inside a virtualized `ItemsRepeater`/`ListBox`. `AgentCardViewModel`: name, status
   badge, spend, headroom hint, attention flag.
3. **Attention derivation:** pure helper — attention = state ∈ {AwaitingReview, Conflict,
   Blocked/waiting-on-input} or coordinator has a pending plan approval (P2-14 feeds this
   later; the derivation function ships now and is unit-tested).
4. **Resource monitor:** poll daemon snapshot (VM CPU/RAM — daemon exposes `/proc` readings via
   an existing/gateway RPC) into fixed-length ring buffers rendered as sparkline `Polyline`s;
   token counters from spend stream. Timer owned by the VM, stopped on Dispose.
5. **`AgentWorkspaceViewModel`:** dock factory building Terminal (P2-03), diff (T-13 against
   the agent worktree path via the SC-2-resolved sync-remote fetch (`mainguard-vm` on WSL2, P2-06) — read-only), staging panel; layout
   persisted per agent kind via `DockLayoutPersistence` (JSON in appdata). Restore falls back to
   default layout on schema drift.
6. **Notifications:** `AgentNotificationService` — on state transition into waiting/blocked, OS
   toast (Windows notification API via Avalonia's `WindowNotificationManager` fallback if native
   unavailable); suppress when `MainWindow.IsActive && current workspace == that agent`.
7. **Teardown:** workspace Dispose closes floating windows (`DockControl` enumeration), stops
   timers, unsubscribes messenger. Wire agent-terminated events (P2-09) → workspace disposal.

---

## 4. Invariants (MUST)

1. Open/close an agent tab **50×** → stable heap + zero floating windows (blocking memory test).
2. All colors via design tokens (v1 rules); the converter is the only status→brush site.
3. `WeakReferenceMessenger` only; no static strong event handlers to workspace VMs.
4. Activity bar stays responsive with 20 simulated agents (virtualization verified).

---

## 5. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `StatusBrush_MappingComplete` | every `AgentStatus` value → a resolvable token key in every theme (enumerate theme dictionaries) |
| 2 | `ActivityBar_LifoOrdering` | spawn A,B,C → list order C,B,A; removal keeps order |
| 3 | `Attention_Derivation` | table-driven: states/plan-pending → expected flag |
| 4 | `DockTeardown_50x_MemoryStable` | open/close 50× → heap growth under threshold, `DockControl` floating windows == 0 (headless) |
| 5 | `ActivityBar_HeadlessPng_AllThemes` | render bar with 4 fake agents in each of the five themes → PNGs written as artifacts (visual review) |
| 6 | `Notifications_SuppressedWhenForegrounded` | transition while active-on-agent → no toast; otherwise → toast |

---

## 6. Rejection triggers / Reviewer script

**Rejection:** raw colors / `StaticResource` for colors; a second status→brush mapping; timers
not stopped on Dispose; strong messenger subscriptions; dock logic in `Mainguard.Agents`.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~AgentStatusBrush|FullyQualifiedName~ActivityBar|FullyQualifiedName~Attention|FullyQualifiedName~DockTeardown"
grep -rn "StaticResource.*Brush\|#[0-9A-Fa-f]\{6\}" Mainguard.App.Shell/Views/Agents/ Mainguard.App.Shell/ViewModels/Agents/   # 0 hits
grep -rn "Dock.Avalonia\|DockControl" Mainguard.Agents/    # 0 hits
```

---

## 7. Definition of done

- [ ] Activity bar (resource monitor, pinned tabs + attention pulse, virtualized LIFO list) streaming live daemon state.
- [ ] Per-agent dock workspaces (terminal/diff/staging) with persisted layouts.
- [ ] Status tokens in all five themes; one converter; OS notifications with foreground suppression.
- [ ] 50× teardown memory test green; headless theme PNGs attached to the PR.
- [ ] Integrated per ControlCenterDesign §0: section rail in MainWindow (no separate window), kill-switch slot at the rail foot in every section, exactly two layouts persisted as `UserPreferences.WorkspaceLayout`, prototype mock services swapped for `DaemonClient` with zero View changes.
- [ ] Test contract = union of the table above and TI-P2-13 (incl. the 50× memory harness and the five-theme render PNGs).
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-13**, base `phase2`.
