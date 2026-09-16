# T-09 — Rich Commit-Graph Interactions — Implementation Plan

**Task ID:** T-09 · **Milestone:** M3 (audit 2.2) · **Priority:** P0
**Depends on:** T-05 (tag actions in menus); the drag-rebase flyout item wants T-08.
**Branch:** `plan/T-09-graph-interactions` → implement on `feat/T-09-graph-interactions` off `main`.

> **Source of truth:** §T-09 of the Master Doc, §TI-09 of the Test Strategy.

---

## 0. Context

The graph renders fast already (`CommitGraphCanvas` + `CommitGraphRouter`), and most actions exist in
`GitService` (`CheckoutRevision`, `ResetToCommit(mode)`, `RevertCommit`, `CherryPick`, `CreateBranch`, and
now `CreateTag`/`CheckoutTag` from T-05). This task wires them into the graph: right-click context menus,
drag-drop merge/rebase between branch labels, branch pinning + "current branch only" filtering, and keyboard
actions. The centerpiece deliverable is a **pure, unit-testable hit-tester** so menu targeting isn't buried
untestably in the control.

### What you can rely on

| Fact | Where |
|---|---|
| Graph is a **per-row** control: one `CommitGraphCanvas` per commit row inside the list; `laneSpacing = 15.0`, dot at `lane*laneSpacing + laneSpacing/2`, `dotY = Bounds.Height/2` | `CommitGraphCanvas.cs:67-94` |
| Router assigns `GraphNode.RowIndex` / `LaneIndex`; `GraphLine.FromLane/ToLane`; `ActiveLanes` fringe | `Core/Graph/GraphModels.cs`, `CommitGraphRouter.cs` |
| `ResetToCommit(string repoPath, string commitSha, LibGit2Sharp.ResetMode mode)` exists (Soft/Mixed/Hard) | `GitServices.cs:1260` |
| Menu tree pattern: `MenuItemViewModel` (+ `SeparatorViewModel`) already used by the branch browser | `App/ViewModels/MenuItemViewModel.cs`, `BranchBrowserViewModel.cs` |
| Persistence: EF Core `AppDbContext` with `DbSet<>` + `Migrations/`; SQLite; migrations run on startup | `Core/AppDbContext.cs:12-13`, `Core/Migrations/` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.App.Shell/Controls/GraphHitTester.cs` (pure math) |
| **Edit** | `Mainguard.App.Shell/Controls/CommitGraphCanvas.cs` (pointer → hit; raise context-menu / drag events) |
| **Edit** | `Mainguard.App.Shell/ViewModels/CommitTimelineViewModel.cs` (menu construction; pin/filter state; commands) |
| **Create** | `Mainguard.Agents/Models/PinnedRef.cs` + `DbSet<PinnedRef>` in `AppDbContext` + **EF migration** |
| **Edit** | `CommitGraphRouter` input path (pinned refs ordered first; current-branch-only filter) |
| **Create** | `Mainguard.Tests/GraphHitTesterTests.cs` (pure), `CommitTimelineMenuTests.cs` (ViewModel, TI-00) |

---

## 2. Contract (must exist exactly)

```csharp
// Mainguard.App.Shell/Controls/GraphHitTester.cs
namespace Mainguard.App.Shell.Controls;

public enum GraphHitKind { None, Node, Label }
public readonly record struct GraphHit(GraphHitKind Kind, string? Sha, string? RefName);

public sealed class GraphHitTester
{
    public GraphHitTester(double rowHeight, double laneWidth, double nodeRadius, double hitSlop);
    public void SetLabelBounds(IReadOnlyList<(Avalonia.Rect Bounds, string RefName, string Sha)> frame); // per render pass
    public GraphHit HitTest(Avalonia.Point p, double verticalScrollOffset,
        IReadOnlyList<(int RowIndex, int LaneIndex, string Sha)> nodes);
}
```

`ResetToCommit` already supports `ResetMode`; expose Soft/Mixed/Hard in the menu, **Hard behind the
confirmation dialog**.

---

## 3. Implementation

### 3.1 Hit-testing (pure, unit-tested)

```csharp
public GraphHit HitTest(Point p, double scrollY, IReadOnlyList<(int RowIndex,int LaneIndex,string Sha)> nodes)
{
    // Labels win over nodes.
    foreach (var (bounds, refName, sha) in _labels)
        if (bounds.Contains(p)) return new GraphHit(GraphHitKind.Label, sha, refName);

    int row = (int)((p.Y + scrollY) / _rowHeight);
    foreach (var n in nodes)
    {
        if (n.RowIndex != row) continue;
        double laneCenterX = n.LaneIndex * _laneWidth + _laneWidth / 2;
        if (Math.Abs(p.X - laneCenterX) <= _nodeRadius + _hitSlop)
            return new GraphHit(GraphHitKind.Node, n.Sha, null);
    }
    return new GraphHit(GraphHitKind.None, null, null);
}
```

Keep it free of Avalonia control types beyond `Point`/`Rect` so it unit-tests (TI-09 #1). In the per-row
canvas reality, `laneWidth == laneSpacing (15.0)` and `nodeRadius` = the canvas dot radius; a single-row
`HitTest` call uses `scrollY = 0` and `RowIndex = 0`, but the tester must still round rows correctly for the
scrolling-canvas configuration the tests exercise (offsets 0 / half-row / large).

### 3.2 Context menus (in `CommitTimelineViewModel`, testable)

On right-click with a `Node` hit, build a `MenuItemViewModel` tree bound to existing commands:
- **Checkout** (detached) · **Create branch here** · **Create tag here** (T-05 dialog) · **Cherry-pick** ·
  **Revert** · **Reset current branch here →** Soft · Mixed · Hard *(confirm)* · **Interactive rebase onto
  here** (T-08) · **Copy SHA**.
- On a `Label` hit → the existing branch-label menu (Phase-4.3), plus tag actions for tag labels.

**Context rules (TI-09 #2):** detached HEAD → hide "Reset current branch here"; menu on the HEAD commit →
hide "Checkout"; unborn/empty graph → no menu. Menu **construction** lives in the ViewModel (testable);
the canvas only raises the event with the `GraphHit`.

### 3.3 Drag-and-drop merge/rebase

Drag branch-label A onto label B → flyout with exactly two actions: **"Merge A into B"** and **"Rebase A
onto B"**. v1 requires B checked out for merge; otherwise the flyout action text becomes **"Checkout B, then
merge A"**. Implemented in-graph via Avalonia `DragDrop` on label elements; **never** merge in-memory against
a non-checked-out branch (rejection trigger).

### 3.4 Pinning + filtering (persisted)

```csharp
// Mainguard.Agents/Models/PinnedRef.cs
public sealed class PinnedRef { public int Id { get; set; } public string RepoPath { get; set; } = ""; public string RefName { get; set; } = ""; public int Order { get; set; } }
```

- Add `DbSet<PinnedRef> PinnedRefs` to `AppDbContext` and generate a migration
  (`dotnet ef migrations add AddPinnedRefs` — matches the existing `Migrations/` flow; migrations run on
  startup).
- Pinned refs are ordered **first** into the `CommitGraphRouter` input (earlier refs get left-most lanes;
  the router already enforces left-most dominance).
- **"Current branch only"** toggle rebuilds the walk with `CommitFilter.IncludeReachableFrom = { HEAD,
  upstream }`.

### 3.5 Keyboard

`Delete` on a selected branch label → delete branch through the existing safety dialog.

---

## 4. Invariants (MUST) / Rejection triggers

**MUST:** hit-testing math is pure and unit-tested at scroll offsets 0/half/large; every menu action routes
through async/`IsBusy` + typed-exception handling; **hard reset always confirms**; pinned refs persist
across restart.

**Rejection triggers:** hit-testing buried untestably in the canvas; menu commands calling `IGitService`
synchronously on the UI thread; a merge implemented in-memory against a non-checked-out branch.

---

## 5. Test contract — TI-09

`GraphHitTesterTests.cs` (pure):
1. `[Theory]` table — node-center hit; just-outside-slop miss; row rounding at scroll offsets 0 / half-row /
   large; label rect hit **wins** over node; empty space → `None`.

`CommitTimelineMenuTests.cs` (ViewModel, needs TI-00):
2. `CommitMenu_ShouldHideResetItems_WhenDetachedHead`; `ShouldHideCheckout_OnHeadCommit`.
3. `HardReset_ShouldRequireConfirmation` — a fake confirmation service records the ask; the action only runs
   after confirm.
4. Routing-only: reset-mode wiring calls `ResetToCommit` with the right `ResetMode` (git semantics already
   covered by backfill B-10).
5. `PinnedRefs_ShouldPersist_AndOrderFirst` — `AppDbContext` round-trip + router input ordering assertion.

---

## 6. Reviewer script / Definition of done

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~GraphHitTester|FullyQualifiedName~CommitTimelineMenu|FullyQualifiedName~PinnedRefs"
# migration present & model snapshot updated:
ls Mainguard.Agents/Migrations | grep -i PinnedRefs
```

- [ ] Pure `GraphHitTester` + tests at multiple scroll offsets.
- [ ] Context menus (commit + label) with context rules; all actions async + typed-exception routed.
- [ ] Hard reset gated by confirmation.
- [ ] Drag-drop merge/rebase flyout; no in-memory merge on non-checked-out branch.
- [ ] `PinnedRef` entity + migration; pinned-first router ordering; current-branch-only filter; Delete-key branch delete.
- [ ] TI-09 green. One task = one PR linking **T-09**.
```
