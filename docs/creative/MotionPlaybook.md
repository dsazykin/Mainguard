# Mainguard Motion Playbook

**Storyboards that turn the Voice & Delight Bible's motion vocabulary into implementable easing/timing briefs.**

This is a Wave-2 creative doc. It conforms to — and cites — [`../Mainguard_Voice_And_Delight_Bible.md`](../Mainguard_Voice_And_Delight_Bible.md) (§3, rules `M-1`…`M-7`), and inherits its constraints from [`../../DESIGN.md`](../../DESIGN.md) §5–§6 ("Depth & motion", Do's/Don'ts) and [`../../PRODUCT.md`](../../PRODUCT.md) Design Principle 3 ("Precision over decoration"). Every moment below ties to a real surface, control, and token in the [`../../AGENTS.md`](../../AGENTS.md) Repository Map / token contract. Where this doc and those files disagree, they win.

**The one budget, restated.** Motion exists only to *clarify state* (Design Principle 3). It is **opacity and brush only, 120–150ms, no overshoot, no property that triggers layout** (`M-3`). The signature button hover is a fixed global **130ms `BrushTransition`** (DESIGN.md §5). Delight is earned by *restraint*: the app moves only when the movement carries meaning. The loom/weave metaphor is expressed on the desktop as *disciplined fades and round-capped linework* — not as literal weaving animation (that lives on the marketing site's `WeaveHero`/`SuccessWeave`, `M-1` note).

**How to read a brief.** Each has no blank cells: **Moment · Surface/control (real name) · Trigger · Animates (exact property) · Stays STILL · Duration + easing · Token(s) · Avalonia mechanism · Earns delight / stays silent (rule).** A brief marked **[Horizon]** governs the unbuilt `phase2` agent control center — voice/motion *character* future work inherits, not UI to build now (Bible "Scope of tense").

---

## 1. Global timing & easing reference

Named curves used throughout (Avalonia `Easing` classes). **No `BackEase`, `BounceEase`, or `ElasticEase` anywhere** — all three overshoot and read as a toy (`M-5`, "no bounce").

| Curve name here | Avalonia class | Feel | Used for |
|---|---|---|---|
| **ease-out** | `CubicEaseOut` | decelerate into rest, **no overshoot** — "taut and settling" (`M-1`) | entrances / reveals (fade-in) |
| **ease-in** | `CubicEaseIn` | accelerate away | exits (fade-out) |
| **linear** | `LinearEasing` | constant | brush cross-fades, monotonic progress |

| # | Motion role | Real surface / control | Property animated | Duration | Easing | Avalonia mechanism | Bible rule |
|---|---|---|---|---|---|---|---|
| G-1 | Button hover / focus | every classed `Button.*`; input focus border | `Background` / `BorderBrush` | **130ms** (fixed, global) | linear | `BrushTransition` | `M-3`, DESIGN §5 |
| G-2 | Selection appear | `CommitTimelineView` row, `CommandPaletteView` `PaletteRowViewModel` row | `Opacity` of a pre-colored selection adornment | **130ms** | ease-out | `DoubleTransition` | `M-2` |
| G-3 | Earned confirmation | `RepoDashboardView` `Border.toast` pill | `Opacity` | **140ms in / 130ms out** | ease-out / ease-in | `DoubleTransition` | `M-1`, `M-5` |
| G-4 | Overlay + scrim entrance | `CommandPaletteView`, `ConfirmationDialog`, clone/reclone overlays | `Opacity` (scrim + card together) | **140ms** | ease-out | `DoubleTransition` | `M-2`, `M-3` |
| G-5 | Skeleton → content reveal | `AnalyticsView`, `RepoDashboardView` loaders | `Opacity` cross-fade | **140ms in / 130ms out** | ease-out / ease-in | `DoubleTransition` | `M-6` |
| G-6 | Badge / drop-target reveal | T-15 signature badge; T-09b drop target | `Opacity` | **130ms** | ease-out | `DoubleTransition` | `M-2` |
| G-7 | Drag ghost | `LabelDragGesture` (T-09b) drag proxy | `Opacity` (+ per-frame pointer-follow, untimed) | **120ms** fade-in | ease-out | `DoubleTransition` + render-loop position | `M-3` |
| G-8 | Honest progress fill | `CloneDashboardView` `ProgressBar` (`CloneProgressPercent`) | control's intrinsic fill (`Value`) | **≤120ms** tween between reported percents | linear, **monotonic** | bound `Value` + `DoubleTransition` on the bound double | `M-6` |
| G-9 | Theme switch | `ThemeManager.ThemeChanged` | color token values only | **instant (0ms)** | none | `DynamicResource` re-resolve | `M-4` |
| G-10 | Commit-graph paint/scroll | `CommitGraphCanvas` | none (per-frame vector draw) | n/a (60fps) | none | `Render()` / `InvalidateVisual()` | `M-2` |

**Reduced motion (`M-7`).** Every timed row above collapses to an instant state change under a reduced-motion preference. Meaning must survive with zero animation — color, label, and the reserved-space adornment carry the state; motion is never the *only* signal.

**The ghost-flash rule (DESIGN.md §"Depth & motion"), applied to fades.** A `BrushTransition` lerps straight (non-premultiplied) RGBA, and the `Transparent` keyword is `#00FFFFFF` — *white* at 0 alpha. Fading a brush from `Transparent` to an opaque/tinted color ramps alpha while RGB is still white, so it **flashes white**. Therefore selection, drop-target, and badge reveals below **fade the `Opacity` of a pre-colored layer** (`DoubleTransition`) rather than cross-fading a brush up from `Transparent`. Opaque-fill buttons (`.Primary`/`.Accent`/`.Success`/`.Danger`) are unaffected and keep the plain G-1 `BrushTransition`; ghost-style controls rest on `SurfaceHoverGhost` (0-alpha `SurfaceHover`).

---

## 2. Signature moment storyboards

### 2.1 Commit-graph render & scroll — **silent** (`M-2`)

| Field | Spec |
|---|---|
| **Surface/control** | `CommitGraphCanvas` (`Mainguard.App.Shell/Controls/CommitGraphCanvas.cs`), the vector DAG lane router |
| **Trigger** | Repository loads / new commits arrive / the timeline scrolls |
| **Animates** | **Nothing timed.** The canvas paints in `Render(DrawingContext)` with `AffectsRender` invalidation; scroll is virtualization, not choreography |
| **Stays STILL** | Every lane, dot, and edge — no per-row entrance, no staggered draw-in, no fade-per-commit. The graph *appears composed*, like an instrument readout |
| **Duration + easing** | None — a 60fps direct paint; frame cadence is the display's, not an eased transition |
| **Token(s)** | `Lane1`–`Lane5` (pens), re-resolved on `ThemeManager.ThemeChanged` via `TryGetResource` |
| **Mechanism** | `Render()` + `InvalidateVisual()`; `PenLineCap.Round` linework carries the "thread" character in *rendering*, not motion |
| **Delight verdict** | **Silent.** The graph is a readout; animating it would read as a hobby diagram (`M-2`, `M-5`). Restraint here *is* the precision. |

### 2.2 Selection rail — **silent, standard fade** (`M-2`)

| Field | Spec |
|---|---|
| **Surface/control** | Selected row's 3px `AccentBrush` left rail + `AccentSelection` fill — `CommitTimelineView` commit rows and `CommandPaletteView` `Border.selected` (`BorderThickness="3,0,0,0"`) |
| **Trigger** | Row becomes selected (click / arrow-key move) |
| **Animates** | `Opacity` **0 → 1** of a pre-composed selection adornment layer (fill = `AccentSelection`, rail = `AccentBrush`, already the right color) |
| **Stays STILL** | The rail's **column is pre-reserved** (DESIGN.md §5) so nothing shifts; rail thickness, row geometry, and text position never move. **No slide, no grow** (`M-2`) |
| **Duration + easing** | 130ms, ease-out (G-2) |
| **Token(s)** | `AccentSelection` (fill), `AccentBrush` (rail) |
| **Mechanism** | `DoubleTransition` on the adornment's `Opacity` — **not** a `BrushTransition` from `Transparent` (avoids the white-flash; §1 ghost-flash rule). Under reduced motion, the adornment snaps to `Opacity=1` |
| **Delight verdict** | **Silent.** Selection is state, not celebration; the fade is the same disciplined 130ms as everything else. |

### 2.3 Hover cross-fade — **silent, free** (`M-3`, DESIGN §5)

| Field | Spec |
|---|---|
| **Surface/control** | Every classed button (`Button.Primary/Accent/Success/Danger/Secondary/IconButton/Pill/Segment`); input `:focus` border |
| **Trigger** | Pointer enters/leaves a button; input gains/loses focus |
| **Animates** | `Background` (hover) and `BorderBrush` (focus) cross-fade |
| **Stays STILL** | Size, corner radius, position, text — only the fill/border *color* moves |
| **Duration + easing** | **130ms**, linear — the one fixed global transition; do **not** add a per-view hover animation |
| **Token(s)** | rest→`SurfaceHover` for ghost buttons (resting on `SurfaceHoverGhost`, 0-alpha `SurfaceHover`, so only alpha changes and no white flash); `AccentHover` for accent hover; `AccentBrush` for focus border |
| **Mechanism** | `BrushTransition` declared once in `App.axaml` |
| **Delight verdict** | **Silent/utility.** It clarifies affordance (hoverable/focused), nothing more. |

### 2.4 Overlay + scrim entrance — **silent, gravity not play** (`M-2`, `M-3`)

| Field | Spec |
|---|---|
| **Surface/control** | Command palette (`CommandPaletteView`, radius-12 `SurfacePanel` card, `BoxShadow 0 10 30 0 #A0000000`); confirmation dialogs (`ConfirmationDialog`; the inline reclone/clone-progress overlays in `CloneDashboardView` on `#80000000`/`#C0000000` scrims) |
| **Trigger** | Palette invoked; a destructive/confirmation action requested |
| **Animates** | `Opacity` **0 → 1** of the scrim and the floating card *together*, as one layer |
| **Stays STILL** | The card **does not scale, pop, bounce, or slide** (`M-2`); it arrives at final size/position and only its opacity resolves. The `BoxShadow` is static |
| **Duration + easing** | 140ms, ease-out (G-4) |
| **Token(s)** | scrim literal `#C0000000`/`#80000000` (allowed scrim exception, DESIGN.md §"Allowed literal-color exceptions"); `SurfacePanel` card, `BorderHairline`, `OnAccent`/`TextPrimary` text |
| **Mechanism** | `DoubleTransition` on the overlay root `Opacity`; drive `Opacity` (not `IsVisible`) so the entrance actually eases in, toggling `IsHitTestVisible` with it |
| **Delight verdict** | **Silent.** A destructive dialog must feel like gravity, never play (`V-2`, `V-4`) — no scale-pop earns trust here. |

### 2.5 Toast entrance / exit — **earns delight** (`M-1`, `T-1`)

| Field | Spec |
|---|---|
| **Surface/control** | `RepoDashboardView` `Border.toast` pill (radius-999, `SuccessBrush`/`DangerBrush` fill, `OnAccent` text, `BoxShadow 0 4 14 0 #40000000`), anchored bottom-right |
| **Trigger** | An action completes (e.g. merge lands) → `IsNotificationVisible` |
| **Animates** | `Opacity` **0 → 1** on entrance, **1 → 0** on exit |
| **Stays STILL** | Position (`Margin=20`, bottom-right), size, radius, shadow. **No scale-pop, no slide-up, no bounce** (`M-5`) — one fade, one meaning |
| **Duration + easing** | 140ms ease-out in; 130ms ease-in out (G-3) |
| **Token(s)** | `SuccessBrush` / `DangerBrush` (via `Border.toast` / `.toast.error`), `OnAccent` text |
| **Mechanism** | `DoubleTransition` on the pill's `Opacity`; bind `Opacity` to `IsNotificationVisible` through a bool→double converter and keep the pill mounted during the fade (drop `IsHitTestVisible` at 0) so the **exit** fade plays instead of a hard `IsVisible` cut |
| **Delight verdict** | **Earns — this is the whole celebration.** A single ~140ms fade of `Merged feature into main.` (`T-1`), then rest, then fade out (`M-1`, `M-5`). No confetti. |

### 2.6 Clone progress — **earns delight via honesty** (`M-6`, `M-1`)

| Field | Spec |
|---|---|
| **Surface/control** | `CloneDashboardView` clone-progress overlay `ProgressBar` bound to `CloneProgressPercent` (T-21), on a `#C0000000` scrim |
| **Trigger** | Clone runs (`IsCloning`); `OnTransferProgress`/`OnCheckoutProgress` report real percentages |
| **Animates** | The bar's intrinsic **fill only**, tweened between reported percents so it glides instead of jumping (addresses the in-code `TODO(T-21 human-review)` on fill smoothness) |
| **Stays STILL** | The overlay card, its layout, the `%` label position, the Cancel button. **Nothing reflows**; the fill is the control's data readout, not app layout |
| **Duration + easing** | ≤120ms linear per step, and **strictly monotonic** — the value never decreases (`CloneProgress` is monotonic by contract). An unknown phase shows a neutral indeterminate bar, never a fake number |
| **Token(s)** | `AccentBrush` (fill), `SurfaceCard` (track), `TextMuted` (`%` label), `DangerBrush` (error text) |
| **Mechanism** | Bound `ProgressBar.Value` + a short `DoubleTransition` on the bound double to smooth each reported step; indeterminate phases use `IsIndeterminate` |
| **Delight verdict** | **Earns — the delight is the honesty.** Monotonic, real progress resolving to 100% then handing off to a confirmation pill (§2.5) is the flourish; a fake or jumping bar would violate `M-6`/`V-6`. |

### 2.7 Analytics skeleton → chart reveal — **silent, functional** (`M-6`, `M-2`)

| Field | Spec |
|---|---|
| **Surface/control** | `AnalyticsView`: the `IsLoading` skeleton grid ("Analyzing Repository History…" + indeterminate bar) → the `LiveChartsCore` content `ScrollViewer` |
| **Trigger** | Repository analysis finishes; `IsLoading` flips false (charts gated further by `HasCommitData`, T-22) |
| **Animates** | `Opacity` cross-fade — skeleton **1 → 0**, content **0 → 1** (both occupy the same `Grid` cell, so it's a true cross-fade) |
| **Stays STILL** | Layout — skeleton and content share the cell, so nothing reflows. Critically, the **charts themselves do not draw in**: LiveCharts' built-in series animation is neutralized (near-zero `AnimationsSpeed` / no easing) so bars and pie wedges *appear composed*, matching the graph's readout ethos (`M-2`) |
| **Duration + easing** | content 140ms ease-out in; skeleton 130ms ease-in out (G-5) |
| **Token(s)** | `SurfaceWindow` (skeleton bg), `AccentBrush`/`SurfaceCard` (indeterminate bar), `TextMuted` (label); chart series use `Lane*`/semantic tokens via `AnalyticsViewModel.ThemeSkColor` |
| **Mechanism** | `DoubleTransition` on each layer's `Opacity`, driven by an `IsLoading`→opacity binding (replacing the current hard `IsVisible` toggle); LiveCharts animation disabled |
| **Delight verdict** | **Silent.** A calm reveal of the finished instrument — not a staggered chart-build animation (that would exceed budget and read as decoration). |

### 2.8 Drag-to-rebase ghost + drop-target highlight — **earns clarity, stays quiet** (`M-2` ethos, `C-2`/`T-09b`)

| Field | Spec |
|---|---|
| **Surface/control** | `LabelDragGesture` (T-09b) dragging one ref chip onto another in `CommitTimelineView`; resolves into the `Rebase … onto …` / `Merge … into …` flyout (`C-2`) |
| **Trigger** | Pointer press on a ref chip moves past the ~5px threshold (`LabelDragGesture.IsArmed` → `IsDragging`) |
| **Animates** | **Ghost:** `Opacity` **0 → ~0.7** of a translucent proxy of the dragged chip as the drag begins. **Drop target:** `Opacity` **0 → 1** of a pre-colored `AccentBrush` outline / `AccentSelection` emphasis on the chip currently under the pointer |
| **Stays STILL** | The **source chip stays in place** (the ghost is a copy, not the chip lifting off). The ghost's *position* follows the cursor per-frame (direct pointer-follow, like the graph — untimed, not an eased transition), but its **size/shape never animate**. No layout shift on any chip |
| **Duration + easing** | ghost fade-in 120ms ease-out (G-7); drop-target highlight 130ms ease-out (G-6). No timed motion on the follow itself |
| **Token(s)** | `AccentSelection` (ghost tint + target fill), `AccentBrush` (target outline) |
| **Mechanism** | `DoubleTransition` on the ghost `Opacity` + render-loop position update; `DoubleTransition` on a pre-colored target-highlight layer's `Opacity` (§1 ghost-flash rule — never a `BrushTransition` up from `Transparent`) |
| **Delight verdict** | **Earns clarity, stays quiet.** Motion here exists purely to answer "what am I dragging, where will it land" (Design Principle 3). The *decision* is then a plain confirmation flyout, never an animated commit. |

### 2.9 Theme switch — **silent, state not spectacle** (`M-4`, `M-2`)

| Field | Spec |
|---|---|
| **Surface/control** | Whole window; driven by `ThemeManager.ThemeChanged` (File → Theme) |
| **Trigger** | User picks another of the five themes (Midnight / Daylight / Command Deck / Atelier / Loom Aurora) |
| **Animates** | **Color token values only**, re-resolved live. No wipe, no flash, no reveal, no celebratory cross-fade |
| **Stays STILL** | **Everything else** — layout, positions, opacity, sizes. Shape/spacing/typography are identical across themes by contract, so only color changes |
| **Duration + easing** | **Instant (0ms), no easing.** Because Daylight Loom is *light* and the others dark, motion must never assume a luminance direction (`M-4`) — an eased luminance sweep would betray a "dark" assumption |
| **Token(s)** | every `DynamicResource` token in `Themes/<Key>.axaml`; code-drawn surfaces (`CommitGraphCanvas`, `DiffViewerView` margin, `AnalyticsViewModel.ThemeSkColor`) re-resolve on `ThemeChanged` |
| **Mechanism** | `ThemeManager` swaps the merged `ResourceDictionary`; `DynamicResource` pointers update in one pass. **No `Transition` object is registered on the swap** |
| **Delight verdict** | **Silent.** The calm, instant swap *is* the premium signal; any transition here would read as spectacle (`M-4`, `M-5`). |

### 2.10 Merge / verification success — **the one earned delight** (`M-1`, `M-5`)

| Field | Spec |
|---|---|
| **Surface/control** | The `T-1` confirmation pill fired through `GitServices` on a completed merge/fast-forward — the `RepoDashboardView` `Border.toast` (§2.5) |
| **Trigger** | A merge or rebase lands successfully |
| **Animates** | `Opacity` **0 → 1** of the pill carrying e.g. `Merged feature into main. 12 files changed.` (`T-1`) |
| **Stays STILL** | Position, size, shadow. The desktop expresses the loom "weave" as *this single settling fade* — **no ring-weave, no `SuccessWeave` animation** (that is the marketing site's language, `M-1` note); no confetti, no scale-pop (`M-5`) |
| **Duration + easing** | ~140ms ease-out in ("thread drawn taut and settling without overshoot"), then rest, then ease-in out (G-3) |
| **Token(s)** | `SuccessBrush` (fill), `OnAccent` (text) |
| **Mechanism** | Same `DoubleTransition`-on-`Opacity` pill as §2.5 |
| **Delight verdict** | **Earns — the emotional peak of the shipped client.** One motion, one meaning; the restraint is precisely what makes it feel premium (`M-1`, `M-5`). |

### 2.11 Signing badge appearance — **quiet, functional** (`M-2`, `TT-3`/`V-6`)

| Field | Spec |
|---|---|
| **Surface/control** | T-15 signature badge in `CommitTimelineView` commit rows: a 13×13 `Panel` (`HasSignatureBadge`), showing `SignatureVerifiedIcon` (`SuccessBrush`) / `SignatureWarningIcon` (`WarningBrush` untrusted / `DangerBrush` bad) |
| **Trigger** | Signature status resolves for a commit while `ShowSignatureStatus` is on |
| **Animates** | `Opacity` **0 → 1** of the badge glyph |
| **Stays STILL** | The **13px slot is reserved** so the badge fades in without expanding the row (apply the selection-rail "reserve the column" discipline rather than the current collapse — an animated collapse/expand would be a layout animation, forbidden by `M-3`). The glyph's **color is set instantly and correctly** at reveal (state-driven; no color animation, no flash) |
| **Duration + easing** | 130ms ease-out (G-6) |
| **Token(s)** | `SuccessBrush` (verified), `WarningBrush` (untrusted), `DangerBrush` (bad) — by meaning, never raw hue |
| **Mechanism** | `DoubleTransition` on the badge `Opacity` |
| **Delight verdict** | **Quiet/functional.** It's a trust indicator, not a celebration — it must never overstate verification (`V-6`, `TT-3`), so it appears understated and holds. |

### 2.12 **[Horizon]** Agent state transition (Verifying → Verified) — **earned, but a fade not a fanfare** (`M-1`, `T-4`)

| Field | Spec |
|---|---|
| **Surface/control** | **[Horizon]** future agent status surface (README §2 Semantic Conflict Verification); its `T-4` status toast, e.g. `Loom-3 passed verification — 214 tests green.` |
| **Trigger** | **[Horizon]** a worker agent's pipeline reaches `Verified` (state vocabulary `Verifying`/`Verified`/`Blocked`/`Quarantined`, `N-3`) |
| **Animates** | `Opacity` cross-fade of the status label from `Verifying` to `Verified`, plus the `T-4` pill fade-in |
| **Stays STILL** | Layout of the agent row; the verdict is carried by the *word* (`passed`/`failed`) and token styling, not motion. **No fanfare, no burst** |
| **Duration + easing** | 140ms ease-out (matches G-3) |
| **Token(s)** | `SuccessBrush` by meaning (pill token-driven, never raw green/red — `T-4`) |
| **Mechanism** | `DoubleTransition` on `Opacity` (the same primitive as today's toast) |
| **Delight verdict** | **Earned but restrained — the emotional peak of the control center kept to a fade** (`M-1`). **[Horizon]:** character future work inherits; not UI to build now (Bible "Scope of tense"). |

---

## 3. The "never animate" list

Anything here is off-system. Reserve space, swap instantly, or fade an opacity layer instead.

1. **Any layout property** — `Width`, `Height`, `Margin`, `Padding`, `Grid` row/column sizes, panel dimensions. Reserve the space (selection-rail column §2.2, badge slot §2.11) and fade `Opacity` instead (`M-3`).
2. **Transform slides or scales for entrance** — no slide-up, no grow, no scale-pop. The system is opacity/brush only (`M-2` "No slide, no grow"; `M-5`). Even a render-only translate is excluded to keep the vocabulary clean.
3. **Bounce / elastic / overshoot easings** — `BackEase`, `BounceEase`, `ElasticEase` are banned; motion arrives and stops (`M-5`, "no bounce").
4. **Commit-graph per-row draw-in / staggered load** — the DAG appears composed (`M-2`, §2.1).
5. **Theme-switch wipe, flash, or celebratory / luminance-directional cross-fade** — instant token re-resolve only (`M-4`, §2.9).
6. **Confirmation / overlay card scale-pop** — gravity, not play (`M-2`, §2.4).
7. **Diff rendering & intra-line emphasis animation** — it appears; it is not animated (`M-2`).
8. **Brush cross-fades that ramp alpha up from the `Transparent` keyword** — they flash white; fade a pre-colored layer's `Opacity` or rest on a 0-alpha ghost token instead (DESIGN.md §"Depth & motion").
9. **Fake or non-monotonic progress** — no jumping percentages, no decorative fills; honest or absent (`M-6`, §2.6).
10. **Anything faster than 120ms or slower than 150ms** in the timed set (the hover/focus brush is fixed at 130ms).

---

## 4. Self-gate (conformance)

Applied to all twelve briefs before publish:

- **Property named + on-system:** every brief animates only `Opacity` or a brush/color-fill (or the graph's untimed paint / progress bar's intrinsic fill). ✔
- **Duration in budget:** all timed motion is 120–150ms (hover fixed 130; theme switch is a deliberate 0ms instant re-resolve, `M-4`). ✔
- **Easing named, no overshoot:** ease-out / ease-in / linear only; `Back`/`Bounce`/`Elastic` explicitly banned (§1, "never animate" #3). ✔
- **No layout animation:** no `Width`/`Height`/`Margin` moves; space is reserved (rail column, badge slot) — verified in every "Stays STILL" cell. ✔
- **Real surface + real token:** each moment names a control from the `docs/repo-map/` index and tokens from the DESIGN.md/AGENTS.md contract. ✔
- **Bible rule cited:** every brief cites its `M-#` (and supporting `V-#`/`T-#`/`TT-#`/`N-#`). ✔
- **Delight is earned by restraint:** exactly the moments Bible `M-1` sanctions earn a fade (toast/merge §2.5/§2.10, clone honesty §2.6, [Horizon] verification §2.12); everything else stays silent per `M-2`. ✔
- **Ghost-flash avoided:** selection, drop-target, and badge reveals fade an opacity layer, never a brush up from `Transparent`. ✔
- **No blank cells.** ✔
