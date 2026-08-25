# Mainguard Design System — Foundation Specification

**Status: DESIGN SPEC — decisions for the in-flight code fixes to adopt. No live file is edited by this document.**

This is the Lane A design-system specification for Mainguard's visual foundation. It records the *decisions* — exact token values, icon geometries, accessibility gates, and motion briefs — that the confirmed UI fixes (lane-token collisions, `SignatureBadIcon`, blame-gutter `ThemeChanged`) and all future surface work must conform to. It sits below [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) (where they disagree, they win) and beside the [Voice & Delight Bible](../creative/Mainguard_Voice_And_Delight_Bible.md), whose numbered rules it cites (`N-#`, `M-#`). It supersedes the *proposed lane values* in [`docs/creative/ThemeRefinement.md`](../creative/ThemeRefinement.md) §4.1 (whose diagnosis it confirms and whose verification plan in §7 still governs shipping); ThemeRefinement's diff-background and Loom Meridian proposals are untouched by this document.

Non-negotiables inherited by every part:

- **One design system, four switchable themes** — Midnight Loom (default), Daylight Loom (light), Graphite (macOS-native), Atelier. (Command Deck and Loom Aurora, analyzed throughout this document's historical sections, were retired in the 2026-08 restyle.) Never assume "dark"; every value below is specified per theme. The §1.3/§3.2 gates are now executable policy: `Mainguard.Tests/ThemeContrastGateTests.cs` machine-checks key-set completeness, AA contrast, lane separability under deuteranopia, and terminal legibility on every run.
- **Semantic-role tokens only** (`{DynamicResource}`), never raw color, never `StaticResource` for anything that follows a live theme switch.
- **Fixed shape/spacing/type scale**: radius 6/8/12/999; spacing 4/5/8/10/15/20; the DESIGN.md §3 type ramp.
- **The Quiet Gatehouse north star**: quiet, layered, one signature accent per view. No Electron/VS-Code-extension chrome, no enterprise-SaaS scaffolding.

Document structure — all four parts are authored; the specification is complete:

- **Part 1 — Decoupled commit-graph lane palette** (this document, complete)
- **Part 2 — State encoding & iconography** (this document, complete)
- **Part 3 — Accessibility across the five themes** (this document, complete)
- **Part 4 — Motion & delight vocabulary** (this document, complete)

---

## Part 1 — Decoupled commit-graph lane palette

The `Lane1`–`Lane5` tokens color the commit graph's DAG threads (`Controls/CommitGraphCanvas.cs`, 2 px round-capped pens and commit dots on `SurfacePanel`) and double as the app's only categorical data-viz palette (`Charts/ChartTheme.cs` `CategoricalPalette()`, donut/bar fills on `SurfaceCard` in `AnalyticsView`). DESIGN.md §2's Semantic-Not-Literal rule requires them to be "deliberately decoupled from these semantic colors so graph topology never accidentally implies status." Part 1 verifies that this decoupling was violated, defines the gates that make it real, and specifies the corrected palette per theme.

### 1.1 The defect, verified

Verified against the pre-fix `Mainguard.App.Shell/Themes/*.axaml` (the values `git diff` shows as removed on branch `phase2`). Exact-hex identity means the graph literally painted status colors as topology: in Midnight Watch, any fifth concurrent branch rendered in `WarningBrush` and any sixth in `InfoBrush` — a branch thread that *looks like a warning* for no reason other than its lane index.

| Theme | Exact-hex collisions (lane == token) | Perceptual near-collisions (ΔE00 < 10) |
|---|---|---|
| **Midnight Watch** | `Lane1` == `AccentBrush` `#8B8BF5` · `Lane4` == `WarningBrush` `#E3B341` · `Lane5` == `InfoBrush` `#58A6FF` | — |
| **Day Watch** | `Lane1` == `AccentBrush` `#6467E8` · `Lane4` == `WarningBrush` `#A9770F` · `Lane5` == `InfoBrush` `#0B78CE` | — |
| **Command Deck** | `Lane1` == `AccentBrush` `#2DD4BF` · `Lane3` == `SuccessBrush` `#34D399` · `Lane4` == `WarningBrush` `#FBBF24` · `Lane5` == `InfoBrush` `#38BDF8` | `Lane1` vs `SuccessHover` (ΔE00 9.6) |
| **Atelier** | `Lane1` == `AccentBrush` `#D8A25A` · `Lane3` == `SuccessBrush` `#8FBC7F` | `Lane1` vs `Warning` (7.7) · `Lane4` vs `Info` (5.7) · `Lane5` vs `Danger` (6.4) |
| **Aurora** | `Lane1` == `AccentBrush` `#4FD1C5` · `Lane4` == `WarningBrush` `#FACC6B` · `Lane5` == `InfoBrush` `#60A5FA` | `Lane3` vs `Danger` (6.3) |

Every theme carried at least two status collisions; Command Deck carried four. The defect is confirmed exactly as reported, plus one finding the report understated: **Atelier had no exact `Warning`/`Info` hex collision but was perceptually the worst offender** — four of its five lanes sat within ΔE00 8 of a semantic token, indistinguishable at 2 px stroke width.

### 1.2 Why the in-flight retune is not yet right

The working tree already carries a retune (the ThemeRefinement §4.1 values, applied verbatim). It removes every exact-hex collision — but re-measured against the gates below, it fails in all five themes:

| Theme | In-flight failure | Measured |
|---|---|---|
| Midnight Watch | `Lane4 #E8A217` still *reads* as `WarningBrush` — same amber family, ΔE00 6.5; `Lane5 #0B87F5` vs `InfoBrush` ΔE00 9.6 | decoupling gate miss |
| Day Watch | `Lane5 #138BE6` vs `InfoBrush` ΔE00 7.1; min deuteranopia-lightness gap 0.059 (`Lane1`/`Lane2`) | decoupling + staircase miss |
| Command Deck | `Lane3 #34CF82` vs `SuccessBrush` ΔE00 **4.3** — a hex-distinct color no user can tell from Success at stroke width; min deut-gap 0.059 | decoupling + staircase miss |
| Atelier | `Lane4 #3A698E` at **2.96:1** vs `SurfacePanel` — below the WCAG 1.4.11 3:1 graphical floor (ThemeRefinement itself flagged this ⚠ and shipped it anyway); `Lane5 #DA9A91` vs `DangerHover` ΔE00 5.2 | contrast + decoupling miss |
| Aurora | `Lane5 #0B63C9` at 3.00:1 vs `SurfacePanel` (zero margin, 2.77 vs `SurfaceCard` — fails the chart surface); `Lane3 #F365B1` vs `DangerBrush` ΔE00 6.8; min deut-gap 0.048 | contrast + decoupling + staircase miss |

The lesson encoded in the gates below: **hex inequality is not decoupling.** A lane 4 ΔE00 from Success is the same defect with a different checksum.

### 1.3 The lane gates (named rules)

All figures are computed with: WCAG 2.1 relative-luminance contrast; CIEDE2000 (ΔE00) for perceptual distance; deuteranopia projection (Viénot 1999 linear-RGB method) with "deutL" = HSL lightness of the projected color, matching ThemeRefinement's metric so the two documents' numbers are comparable. These are design-time gates; the shipping gates remain ThemeRefinement §7 (unit-test contrast gate + render harnesses + human CVD spot-check).

**G1 — The Literal-Decoupling Gate.** No lane may be hex-identical to *any* other token in its theme file (all 32 of the AGENTS.md token contract, not just the six semantics).

**G2 — The Perceptual-Decoupling Gate.** Every lane keeps ΔE00 ≥ 10 from each of `SuccessBrush`, `DangerBrush`, `WarningBrush`, `InfoBrush`, and ΔE00 ≥ 8 from `SuccessHover`/`DangerHover` (hover fills appear only transiently on buttons, so the gate is one step softer). This is what "decoupled" means at 2 px stroke width — distance a user can actually see.

**G3 — The Stroke-Contrast Gate.** Every lane clears **3.2:1 against `SurfacePanel`** (the commit graph's surface — the 3:1 WCAG 1.4.11 floor plus margin, so no value ships at the edge the way Atelier's 2.96 did) **and 3.0:1 against `SurfaceCard`** (the Analytics chart surface, per PolishSpec §3's chart-contrast requirement).

**G4 — The Staircase Gate.** Within a theme, the five lanes hold a minimum pairwise deutL gap of **0.070**: under red-green color-vision deficiency the lanes remain orderable by lightness alone, per ThemeRefinement Finding A (violet/teal/rose/sky/indigo all collapse to one deuteranopic hue, so lightness must carry the identity that hue cannot).

**G5 — The Warp-Thread Exception.** `Lane1` is the graph's first and most-used lane — the trunk most repositories render as the main branch. It deliberately stays in its theme's *accent hue family* (hex-distinct, G1 applies): the trunk reads as "this theme's thread," which is identity, not status — `AccentBrush` is the signature accent, not a semantic state (DESIGN.md §2 names only Success/Danger/Warning/Info as status roles). Measured kinship in the corrected palette: Midnight ΔE00 4.7, Command Deck 6.9, Aurora 4.8 from `AccentBrush`. The one place this costs something: a lane-1 thread running through a selected row's `AccentSelection` tint loses a little figure-ground pop; the selection rail (3 px `AccentBrush`, DESIGN.md §5) carries the selection signal there, so nothing is color-only.

Naming stays role-based per Bible N-2/N-5: the tokens are `Lane1`–`Lane5`, never hue names. The prose descriptions below ("ember," "verdigris") are design rationale, not token names.

### 1.4 The corrected lane palette, per theme

Design stance: this is a *retune of the in-flight values*, not a fourth palette. Every in-flight lane that passes all five gates is ratified unchanged (12 of 25 do); the 13 that fail are corrected with the smallest move that passes, keeping each theme's stated character (the theme file headers: Midnight "jewel-tone lanes," Command Deck "tactical," Atelier "craftsman," Aurora "luminous"). Contrast columns are vs `SurfacePanel` / vs `SurfaceCard`; "min sem ΔE00" is the distance to the nearest semantic token (which one, in parentheses).

#### Midnight Watch — jewel tones on cool charcoal

The five threads: violet warp, magenta rose, pale mint, burnt ember, twilight cobalt. `Lane4` moves from amber (Warning's family) to a darker burnt *ember orange* — visibly redder and deeper than `WarningBrush`'s brass. `Lane5` vacates Info's azure register entirely: the deuteranopic-lightness slot it occupied is where `InfoBrush` itself lives, so no saturated azure could ever clear G2 there — the blue lane drops to a deep twilight cobalt instead.

| Token | Value | vs Panel `#14171C` | vs Card `#1A1E24` | deutL | min sem ΔE00 | Disposition |
|---|---|---|---|---|---|---|
| `Lane1` | `#9A9AF4` | 7.10 | 6.62 | 0.780 | 14.9 (Info) | ratified (warp thread, G5) |
| `Lane2` | `#E860A4` | 5.68 | 5.29 | 0.616 | 18.1 (Danger) | ratified |
| `Lane3` | `#C0EAE3` | 13.79 | 12.84 | 0.884 | 22.0 (SuccessHover) | ratified |
| `Lane4` | `#DD7C10` | 5.96 | 5.55 | 0.315 | 18.5 (Warning) | **corrected** (was `#E8A217`, ΔE00 6.5 from Warning) |
| `Lane5` | `#5066B4` | 3.34 | 3.11 | 0.542 | 23.4 (Info) | **corrected** (was `#0B87F5`, ΔE00 9.6 from Info) |

Min pairwise deutL gap **0.074**.

```xml
<!-- Commit-graph lanes (decoupled from semantics — DesignSystem.md Part 1 gates G1–G5) -->
<SolidColorBrush x:Key="Lane1" Color="#9A9AF4"/>
<SolidColorBrush x:Key="Lane2" Color="#E860A4"/>
<SolidColorBrush x:Key="Lane3" Color="#C0EAE3"/>
<SolidColorBrush x:Key="Lane4" Color="#DD7C10"/>
<SolidColorBrush x:Key="Lane5" Color="#5066B4"/>
```

#### Day Watch — ink tones on paper

Deep ink threads that hold weight on a light surface: indigo ink, deep magenta, pine teal, umber, cerulean. `Lane2` deepens one step to open the CVD staircase (the in-flight value sat 0.059 from `Lane1`); `Lane5` shifts off Info's azure toward a cerulean that is both perceptually distinct (ΔE00 12.6) and stronger against paper (3.35:1, up from 3.37 with no margin at 3.2 gate — and now clear of Info's hue).

| Token | Value | vs Panel `#F7F8FB` | vs Card `#FFFFFF` | deutL | min sem ΔE00 | Disposition |
|---|---|---|---|---|---|---|
| `Lane1` | `#3232E2` | 7.35 | 7.81 | 0.541 | 23.3 (Info) | ratified (warp thread; ΔE00 14.9 from Accent — kin by hue, safely distant) |
| `Lane2` | `#B02573` | 5.89 | 6.25 | 0.425 | 21.3 (DangerHover) | **corrected** (was `#C62A83`, staircase gap 0.059) |
| `Lane3` | `#075B55` | 7.50 | 7.96 | 0.320 | 20.3 (SuccessHover) | ratified |
| `Lane4` | `#7B420A` | 7.53 | 8.00 | 0.172 | 20.9 (Warning) | ratified |
| `Lane5` | `#1092C1` | 3.35 | 3.56 | 0.625 | 12.6 (Info) | **corrected** (was `#138BE6`, ΔE00 7.1 from Info) |

Min pairwise deutL gap **0.084** — the widest staircase of the five, as it should be on the theme where "assumed dark" bugs surface (PolishSpec §7).

```xml
<!-- Commit-graph lanes (decoupled from semantics — DesignSystem.md Part 1 gates G1–G5) -->
<SolidColorBrush x:Key="Lane1" Color="#3232E2"/>
<SolidColorBrush x:Key="Lane2" Color="#B02573"/>
<SolidColorBrush x:Key="Lane3" Color="#075B55"/>
<SolidColorBrush x:Key="Lane4" Color="#7B420A"/>
<SolidColorBrush x:Key="Lane5" Color="#1092C1"/>
```

#### Command Deck — tactical traces on near-black

Ice-teal trace, electric violet, signal orange, sand, deep-sea blue. The hard call: the in-flight `Lane3 #34CF82` is indistinguishable from `SuccessBrush` (ΔE00 4.3), and *no green exists* that clears ΔE00 10 from `#34D399`, holds the 0.57–0.60 deutL slot, and is not a volt lime that would out-shout the entire deck. So `Lane3` leaves green: it becomes a **signal orange** — the mission-control trace color, exactly this theme's register — far from `WarningBrush`'s yellow-amber (ΔE00 20+) and from `DangerHover`'s pink (20.4). `Lane2` deepens to an electric violet to restore the staircase gap under the sand of `Lane4`.

| Token | Value | vs Panel `#0E1114` | vs Card `#13171B` | deutL | min sem ΔE00 | Disposition |
|---|---|---|---|---|---|---|
| `Lane1` | `#7AE7D9` | 12.86 | 12.23 | 0.834 | 10.0 (SuccessHover) | ratified (warp thread, G5) |
| `Lane2` | `#8029FA` | 3.26 | 3.10 | 0.645 | 39.0 (Danger) | **corrected** (was `#9D4AF0`, staircase gap 0.059 to Lane4) |
| `Lane3` | `#F39E6D` | 8.95 | 8.51 | 0.574 | 20.4 (DangerHover) | **corrected** (was `#34CF82`, ΔE00 4.3 from Success) |
| `Lane4` | `#FCE091` | 14.62 | 13.90 | 0.738 | 12.4 (Warning) | ratified |
| `Lane5` | `#086EA1` | 3.39 | 3.22 | 0.501 | 26.3 (Info) | ratified |

Min pairwise deutL gap **0.070**.

```xml
<!-- Commit-graph lanes (decoupled from semantics — DesignSystem.md Part 1 gates G1–G5) -->
<SolidColorBrush x:Key="Lane1" Color="#7AE7D9"/>
<SolidColorBrush x:Key="Lane2" Color="#8029FA"/>
<SolidColorBrush x:Key="Lane3" Color="#F39E6D"/>
<SolidColorBrush x:Key="Lane4" Color="#FCE091"/>
<SolidColorBrush x:Key="Lane5" Color="#086EA1"/>
```

#### Atelier — the craftsman's bench

Cream, plum, pale sage, indigo slate, **verdigris**. Two structural corrections: `Lane4` lifts off the sub-floor 2.96:1 to a legible indigo slate at 3.25:1 (kept ΔE00 15.1 from the dusty-blue `InfoBrush` — the blue slot in a warm theme inevitably neighbors Info, and 1.5× the gate is the honest margin available). `Lane5` was the real design problem: the in-flight dusty terracotta sat ΔE00 5.2 from `DangerHover` in a palette whose semantics already own sage, oxblood, and brass — there is no free *warm* hue left in Atelier. The answer is the workshop's own color: **verdigris, the patina of aged copper**, the natural companion of the theme's copper accent, in the only unclaimed hue family (ΔE00 21.2 from Success, 38.8 from the accent). `Lane1`'s cream lightens a half-step so the staircase clears between the verdigris and the sage.

| Token | Value | vs Panel `#1D1A16` | vs Card `#24201A` | deutL | min sem ΔE00 | Disposition |
|---|---|---|---|---|---|---|
| `Lane1` | `#E3C8A2` | 10.77 | 10.06 | 0.725 | 13.4 (Warning) | **corrected** (was `#E4C399`; staircase gap 0.069 → 0.094) |
| `Lane2` | `#AC57A5` | 3.85 | 3.59 | 0.556 | 27.9 (Danger) | ratified |
| `Lane3` | `#C7E0BF` | 12.25 | 11.45 | 0.801 | 8.2 (SuccessHover) | ratified (hover-gate 8 met; ΔE00 from `SuccessBrush` itself ≥ 10) |
| `Lane4` | `#4D6E8F` | 3.25 | 3.04 | 0.481 | 15.1 (Info) | **corrected** (was `#3A698E`, 2.96:1 — below the graphical floor) |
| `Lane5` | `#2CAFA9` | 6.45 | 6.03 | 0.631 | 21.2 (Success) | **corrected** (was `#DA9A91`, ΔE00 5.2 from DangerHover) |

Min pairwise deutL gap **0.075** — up from the shipping palette's 0.06-wide total band that ThemeRefinement called the worst case.

```xml
<!-- Commit-graph lanes (decoupled from semantics — DesignSystem.md Part 1 gates G1–G5) -->
<SolidColorBrush x:Key="Lane1" Color="#E3C8A2"/>
<SolidColorBrush x:Key="Lane2" Color="#AC57A5"/>
<SolidColorBrush x:Key="Lane3" Color="#C7E0BF"/>
<SolidColorBrush x:Key="Lane4" Color="#4D6E8F"/>
<SolidColorBrush x:Key="Lane5" Color="#2CAFA9"/>
```

#### Aurora — luminous night

Aurora teal, luminous violet, fuchsia, pale-gold glow, deep cobalt. `Lane3` shifts from rose (Danger's family, ΔE00 6.8) to a fuchsia-magenta that keeps Aurora's flash while clearing Danger by 14.3. `Lane5` lifts off the 3.00:1 edge to a fuller cobalt at 3.29:1 — and now also clears `SurfaceCard` (3.04, the in-flight value failed the chart surface at 2.77). `Lane2` lightens one step so the violet/fuchsia staircase holds.

| Token | Value | vs Panel `#161930` | vs Card `#1C2038` | deutL | min sem ΔE00 | Disposition |
|---|---|---|---|---|---|---|
| `Lane1` | `#79E0D5` | 11.05 | 10.23 | 0.815 | 16.9 (SuccessHover) | ratified (warp thread, G5) |
| `Lane2` | `#977BF5` | 5.32 | 4.93 | 0.739 | 20.0 (Info) | **corrected** (was `#8B69F3`, staircase gap 0.048 to Lane3) |
| `Lane3` | `#E24FC0` | 5.03 | 4.66 | 0.653 | 14.3 (Danger) | **corrected** (was `#F365B1`, ΔE00 6.8 from Danger) |
| `Lane4` | `#FDF0D7` | 15.32 | 14.18 | 0.899 | 17.5 (Warning) | ratified |
| `Lane5` | `#4566C9` | 3.29 | 3.04 | 0.578 | 21.6 (Info) | **corrected** (was `#0B63C9`, 3.00:1 panel / 2.77:1 card) |

Min pairwise deutL gap **0.075**.

```xml
<!-- Commit-graph lanes (decoupled from semantics — DesignSystem.md Part 1 gates G1–G5) -->
<SolidColorBrush x:Key="Lane1" Color="#79E0D5"/>
<SolidColorBrush x:Key="Lane2" Color="#977BF5"/>
<SolidColorBrush x:Key="Lane3" Color="#E24FC0"/>
<SolidColorBrush x:Key="Lane4" Color="#FDF0D7"/>
<SolidColorBrush x:Key="Lane5" Color="#4566C9"/>
```

### 1.5 Delta from the working tree (for the in-flight fix to apply)

Thirteen values change; twelve are ratified as-is. Per theme file:

| File | Token | Working tree | This spec |
|---|---|---|---|
| `Themes/MidnightLoom.axaml` | `Lane4` | `#E8A217` | `#DD7C10` |
| `Themes/MidnightLoom.axaml` | `Lane5` | `#0B87F5` | `#5066B4` |
| `Themes/DaylightLoom.axaml` | `Lane2` | `#C62A83` | `#B02573` |
| `Themes/DaylightLoom.axaml` | `Lane5` | `#138BE6` | `#1092C1` |
| `Themes/CommandDeck.axaml` | `Lane2` | `#9D4AF0` | `#8029FA` |
| `Themes/CommandDeck.axaml` | `Lane3` | `#34CF82` | `#F39E6D` |
| `Themes/Atelier.axaml` | `Lane1` | `#E4C399` | `#E3C8A2` |
| `Themes/Atelier.axaml` | `Lane4` | `#3A698E` | `#4D6E8F` |
| `Themes/Atelier.axaml` | `Lane5` | `#DA9A91` | `#2CAFA9` |
| `Themes/LoomAurora.axaml` | `Lane2` | `#8B69F3` | `#977BF5` |
| `Themes/LoomAurora.axaml` | `Lane3` | `#F365B1` | `#E24FC0` |
| `Themes/LoomAurora.axaml` | `Lane5` | `#0B63C9` | `#4566C9` |
| *(ratified, no change)* | Midnight `Lane1/2/3` · Daylight `Lane1/3/4` · CommandDeck `Lane1/4/5` · Atelier `Lane2/3` · Aurora `Lane1/4` | — | — |

### 1.6 Application notes for the fixers

1. **Fallback literals still carry the original defect.** `Controls/CommitGraphCanvas.cs` (lines 32–36) and `Charts/ChartTheme.cs` (lines 45–49) hard-code the *pre-fix* Midnight lanes (`#8B8BF5` … `#58A6FF`) as `TryGetResource` fallbacks — including the Warning and Info hexes this whole exercise removes. Update both fallback arrays to this spec's Midnight values (`#9A9AF4`, `#E860A4`, `#C0EAE3`, `#DD7C10`, `#5066B4`). A fallback that reintroduces the collision is the collision.
2. **Keep `ChartTheme`'s draw-order shuffle** (`[Lane1, Lane2, Lane4, Lane3, Lane5]`). Under the corrected palette its worst adjacency is Atelier's sage-next-to-verdigris (Lane3/Lane5) — hue-adjacent but 0.17 apart in deutL, and PolishSpec §3's hairline slice separators + direct labels carry the remaining separation. No reorder change is needed.
3. **Ship through ThemeRefinement §7's gates**, with the thresholds updated to this spec: lanes ≥ 3.2:1 vs `SurfacePanel` *and* ≥ 3.0:1 vs `SurfaceCard`; min pairwise deutL gap ≥ 0.07; ΔE00 ≥ 10 vs the four primary semantic tokens and ≥ 8 vs the two hover tokens; zero exact-hex matches against the full 32-token contract. All 25 values above pass all five gates today (verification data: §1.7). The `LaneSeparabilityRenderHarness` ThemeRefinement recommends is the right home for the deuteranopia-simulated capture.
4. **Comment convention in the theme files**: keep one comment above the lane block pointing here (`DesignSystem.md Part 1`), replacing the per-file "Retuned per ThemeRefinement Finding B" notes, so the next designer finds the gates and not just the values.
5. **Out of Part 1's scope**: the diff add/remove backgrounds (ThemeRefinement §4.2 remains the proposal of record; Part 3 will gate them), `BlameAgeNew/Old` (a sequential ramp, inherently CVD-safe), and the proposed Loom Meridian sixth theme.

### 1.7 Verification record

Method: WCAG 2.1 relative luminance for contrast; Viénot 1999 deuteranopia projection with HSL-lightness readout (comparable to ThemeRefinement's deutL); CIEDE2000 for perceptual distance. Summary across all 25 corrected values:

- Exact-hex collisions vs any theme token: **0** (was 14 pre-fix).
- Minimum lane contrast vs `SurfacePanel`: **3.25:1** (Atelier `Lane4`); vs `SurfaceCard`: **3.04:1** (Atelier `Lane4`, Aurora `Lane5`). Nothing at the 3.0 edge.
- Minimum ΔE00 to a primary semantic token: **12.4** (Command Deck `Lane4` vs `WarningBrush`); to a hover token: **8.2** (Atelier `Lane3` vs `SuccessHover`).
- Minimum pairwise deutL gap, per theme: Midnight 0.074 · Daylight 0.084 · Command Deck 0.070 · Atelier 0.075 · Aurora 0.075 (in-flight: 0.073 / 0.059 / 0.059 / 0.069 / 0.048).
- Warp-thread (G5) kinships, documented: Midnight `Lane1` ΔE00 4.7 from `AccentBrush`, Command Deck 6.9, Aurora 4.8; Daylight 14.9 and Atelier 12.7 (their accents needed no closeness).

---

## Part 2 — State encoding & iconography

Part 2 governs how *categorical state* is encoded anywhere in the app: signature trust, check outcomes, finding severity, diff change kind, chip status. The governing principle is the Bible's own, extended from motion and hover to color: **meaning must survive with zero color** — the visual analogue of M-7 ("meaning must survive with zero animation") and TT-4 ("meaning survives with zero hover"). Color remains a channel — the semantic brushes stay exactly where they are — but it is never the *only* channel. This also serves V-2 directly: when shape carries the state, color no longer has to shout.

Icons remain theme-independent `StreamGeometry` resources in `App.axaml`, referenced by `StaticResource`, on the 24×24 grid, at the AGENTS.md icon scale (14×14 inline, 10–12 adornments) — nothing in this part adds a color token or touches a theme file.

### 2.1 The defect, verified — and why the in-flight patch is not yet right

**Pre-fix (the confirmed defect).** In the pre-fix `CommitTimelineView.axaml` (lines 249–250 territory), the signing badge bound *both* `IsSignatureUntrusted` and `IsSignatureBad` (`CommitRowViewModel.cs` lines 46/53) to the same `SignatureWarningIcon`; only the `Foreground` differed (`WarningBrush` vs `DangerBrush`). No `SignatureBadIcon` existed in `App.axaml`. "Can't verify this signature" and "the contents do not match this signature" — a routine state and a possible-tampering state — were literally the same picture in a different color. Confirmed exactly as reported, and PolishSpec §1 independently flags the same block.

**The in-flight patch (working tree).** `App.axaml` line 69 now carries a `SignatureBadIcon` (the filled shield with an X cutout, the MDI shield-remove construction), bound at `CommitTimelineView.axaml:251`. Re-measured against the gates below, it is better but not right:

1. **Glyph inequality is not shape distinction** — the Part 1 lesson with a different checksum. All three states are the *same filled-shield silhouette* distinguished by a ~5-unit inner cutout, and the pair that matters most — verified (check) vs bad (X) — differs by two small diagonal marks rendered at 13 px. In grayscale at that size the badge fails the glance test the fix exists to pass.
2. **The PolishSpec §1 decisions were not adopted.** The badge is still `13×13` (off the AGENTS.md icon scale) and the holder `Panel` (line 248) collapses via `IsVisible="{Binding HasSignatureBadge}"`, so a signed commit's message sits 18 px right of an unsigned one — state changes layout.

### 2.2 The encoding gates (named rules)

These extend Part 1's G1–G5 (lane gates) with four *encoding* gates. They apply to every categorical state surface, current and future — including the Horizon agent-status badges, per PolishSpec's note that agent work inherits the shape-distinct rule.

**E1 — The Zero-Color Gate.** Any two states of the same fact must differ by at least one non-color channel — silhouette, fill mode (solid/hollow), glyph, weight, or visible text — such that the pair is distinguishable in a grayscale render at the size it actually ships. Brush difference alone is never an encoding. (M-7 / TT-4, applied to hue.)

**E2 — The Shared-Silhouette Family Rule.** All states of *one* fact share one base silhouette (trust = the shield; severity = a filled primitive with a knocked-out mark), so the column reads as one instrument whose state varies — and different facts never share a base, so a severity mark can never be misread as a trust mark. Within the family, states differ by fill mode and internal structure, not by swapping metaphors.

**E3 — The Reserved-Slot Gate.** A state badge lives in a fixed-width holder that is always present; state changes toggle the *content* (child visibility/opacity), never the slot. Nothing reflows when a state appears. Precedents: the selection rail's always-reserved column (DESIGN.md §5) and PolishSpec §1's fixed-width holder.

**E4 — The Text-Anchor Gate.** A pill or chip that encodes status contains its own label text (`LfsWindow`'s `StatusLabel`, `SubmodulesWindow`'s status chips, `WorktreeWindow`'s "locked"). A bare colored dot is never a state carrier — a dot may *accompany* a label, never replace one. (TT-4's "a tooltip is supplementary, never the sole carrier" applies equally to hue.)

### 2.3 The signature triad — solid, hollow, fractured

The design: keep one shield family (E2 — trust *is* the shield) and make the **silhouette and fill mode** carry the state, with the inner mark and the semantic brush as second and third redundant channels:

| State (`SignatureStatus`) | Form | Meaning encoded by the form | Icon key | Brush | VM flag → view binding |
|---|---|---|---|---|---|
| Good | **Solid** shield, check cutout | Protection confirmed — full seal | `SignatureVerifiedIcon` | `SuccessBrush` | `IsSignatureVerified` (`CommitRowViewModel.cs:43`) → `CommitTimelineView.axaml:249` |
| Untrusted / can't verify | **Hollow** shield (outline ring), exclamation inside | The outline of protection without its substance — present, unconfirmed | `SignatureWarningIcon` | `WarningBrush` | `IsSignatureUntrusted` (`:46`) → `:250` |
| Bad / mismatch | **Fractured** shield — the rim breached, a lightning crack through the body | The seal is broken | `SignatureBadIcon` | `DangerBrush` | `IsSignatureBad` (`:53`) → `:251` |

This also puts visual weight where frequency and severity want it: in a repo whose keys you haven't imported, *most* rows are untrusted — the quiet ring recedes (V-2); verified is an affirmative solid; bad is rare and unmistakable. The fractured form is semantically truer than the in-flight X: a bad signature is not "removed" or "blocked," it is a seal that no longer holds.

**Exact geometries** (24×24 grid; all three share the identical outer shield base path, per E2):

```xml
<!-- Signing badges (T-15 · DesignSystem.md Part 2 §2.3): one shield family, three silhouettes —
     solid (verified) / hollow (untrusted) / fractured (bad) — so trust reads with zero color. -->
<StreamGeometry x:Key="SignatureVerifiedIcon">M12,1L3,5V11C3,16.55 6.84,21.74 12,23C17.16,21.74 21,16.55 21,11V5L12,1M10,17L6,13L7.41,11.59L10,14.17L16.59,7.58L18,9L10,17Z</StreamGeometry>
<StreamGeometry x:Key="SignatureWarningIcon">M12,1L3,5V11C3,16.55 6.84,21.74 12,23C17.16,21.74 21,16.55 21,11V5L12,1M12,3.18L19,6.3V11.22C19,15.54 16.14,19.29 12,20.71C7.86,19.29 5,15.54 5,11.22V6.3L12,3.18M13,17H11V15H13V17M13,13H11V7H13V13Z</StreamGeometry>
<StreamGeometry x:Key="SignatureBadIcon">M10.7,1.58L9.8,7.6L11.9,12.3L10.9,18L14.1,12.3L12,7.6L13.3,1.58L21,5V11C21,16.55 17.16,21.74 12,23C6.84,21.74 3,16.55 3,11V5L10.7,1.58Z</StreamGeometry>
```

Construction notes, so the next designer can verify rather than trust:

- **Verified is ratified unchanged** — the existing solid shield-check. The smallest move that passes (the Part 1 principle) is to not move it.
- **Untrusted keeps its exclamation marks at the exact existing coordinates** (`M13,17H11V15H13V17M13,13H11V7H13V13`); the correction only inserts the 2-unit-inset inner shield subpath (`M12,3.18L19,6.3V11.22C19,15.54 16.14,19.29 12,20.71C7.86,19.29 5,15.54 5,11.22V6.3L12,3.18`), which hollows the fill into a ring under Avalonia's default even-odd fill rule (outer = fill, inset = hole, marks = fill again). If a fixer ever prepends `F1` (nonzero), the inset subpath must be re-wound counter to the outer — with no prefix, as everywhere else in `App.axaml`, it works as written.
- **Bad is a single self-intersection-free subpath**: the outline runs up the top-left edge to the left crack lip `(10.7,1.58)`, descends the crack's left wall `(9.8,7.6) → (11.9,12.3)` to the tip `(10.9,18)`, climbs the right wall `(14.1,12.3) → (12,7.6)` to the right lip `(13.3,1.58)`, and resumes the normal shield boundary. Both lips sit exactly on the shield's top edges (`4x+9y=57` and `4x−9y=39`), the apex `(12,1)` falls inside the 2.6-unit mouth and is consumed — the *outer contour itself is breached*, which is what survives at 12 px — and the channel holds a constant 2.2-unit gap down to an off-center tip that stays well inside the bottom curves (shield spans x ≈ 5.1–18.9 at y = 18). No inner X: one strong mark, not two small ones.

**Grayscale readout** (the E1 self-test, at the shipping 12 px):

| State | Ink mass | What the eye gets with zero color |
|---|---|---|
| Verified | ~85 % solid | a heavy seal with a tick |
| Untrusted | ~25 % — a 1 px ring + marks | an empty outline |
| Bad | ~80 %, top rim broken | a shield split by lightning |

Solid / hollow / broken is legible at a glance where check-cutout / !-cutout / X-cutout was not.

**Placement and size — PolishSpec §1 ratified.** The badge and each `PathIcon` go to **12×12** (icon-adornment scale; the current 13 is off-scale), inside a fixed-width holder that is *always present* when the timeline renders (E3): toggle the glyphs' visibility on `HasSignatureBadge`, never the holder, so the message line's left edge is identical across signed and unsigned commits. The trailing gap stays `5` (on the spacing scale). `SignatureTooltip` (`CommitRowViewModel.cs:56`) remains the precise-wording channel per TT-3 — supplementary, since the glyph now carries the state on its own (TT-4).

**Naming note (N-3).** `SignatureWarningIcon` names the brush, not the state; `SignatureUntrustedIcon` would name what is true. The rename is optional polish, not gated — it touches `CommitTimelineView`, PolishSpec §1, and the AGENTS.md map for zero user-visible gain — but any *new* state icon must name its state, not its color.

### 2.4 The severity triad — octagon, triangle, circle

Two surfaces encode severity as a bare 8 px colored dot — identical circles, brush-only, failing E1 and E4:

- `PreCommitFindingsView.axaml:73–77` — group headers: `DangerBrush` (blocker) / `WarningBrush` (warning) / `InfoBrush` (info).
- `CommitComposerView.axaml:147–152` — validation issues: `DangerBrush` (error) / `WarningBrush` (advisory).

The remedy is one shared severity vocabulary — three distinct silhouettes with a knocked-out inner mark, the established stop/caution/inform triad, sized 10×10 (adornment scale) where the dots sit today:

```xml
<!-- Severity glyphs (DesignSystem.md Part 2 §2.4): silhouette carries severity — octagon (blocker),
     triangle (warning, the existing WarningIcon), circle (info) — color is the second channel. -->
<StreamGeometry x:Key="SeverityBlockerIcon">M7.86,2L16.14,2L22,7.86V16.14L16.14,22H7.86L2,16.14V7.86L7.86,2M7,10.75H17V13.25H7V10.75Z</StreamGeometry>
<StreamGeometry x:Key="SeverityInfoIcon">M12,2C6.48,2 2,6.48 2,12C2,17.52 6.48,22 12,22C17.52,22 22,17.52 22,12C22,6.48 17.52,2 12,2M13,9H11V7H13V9M13,17H11V11H13V17Z</StreamGeometry>
```

- **Blocker** — the filled octagon with a knocked-out bar: the stop-sign silhouette, flat-topped and bar-struck, unmistakable against a circle even at 10 px. It says "this stops the commit," which is exactly what `AwaitingOverride` means, without adding volume (V-2: severity by role, not by louder words).
- **Warning** — reuse the existing `WarningIcon` triangle (`App.axaml:41`), which already carries its exclamation cutout. No new geometry; the triangle is already the app's caution silhouette (`BlameView:35`, `DiffViewerView:86/163`, `LfsWindow:28`).
- **Info** — the filled circle with a knocked-out ⅰ, built with the same cubic-circle construction as the existing `IssueIcon`.

**Bindings:** in `PreCommitFindingsView`, replace the three overlapped `Border` dots with three overlapped 10×10 `PathIcon`s on the same `IsBlocker`/`IsWarning`/`IsInfo` flags and the same brushes. In `CommitComposerView`, `IsError` → `SeverityBlockerIcon` + `DangerBrush`, else `WarningIcon` + `WarningBrush` (the widened `Panel` stays fixed-width per E3). The finding *rows'* kind icons (`KeyIcon`/`WarningIcon`/`DocumentIcon`, lines 88–95) already encode their kind by shape and their message by text and pass as-is — with one optional refinement: `IsManyFiles` currently shares `DocumentIcon` with `IsLargeFile` (kind survives via the message text, so it passes E1, but `FolderIcon` would be the truer glyph and is free).

### 2.5 The diff change-bar remedy — solid vs hollow

`DiffViewerView.axaml.cs` (`DiffMarginRenderer.Draw`, ~line 190) renders added and modified lines as **identical 4 px bars** distinguished only by `SuccessBrush` vs `AccentBrush` — brush-only, an E1 failure in the surface developers stare at longest. The remedy reuses the triad's fill-mode axis, which costs no width and no new tokens:

- **Added** — the solid 4 px bar, `SuccessBrush` (unchanged).
- **Modified** — the same 4 px slot rendered **hollow**: a 1 px `AccentBrush` stroke around an unfilled core (draw the rect with a `Pen`, no fill).

Solid-vs-hollow survives grayscale at any zoom, keeps the gutter geometry fixed (E3), and echoes §2.3's vocabulary: full state = solid, partial state = outline. Deleted lines are not currently rendered in this margin at all; when they are, the convention is the third form — a small solid triangle marker at the seam, a distinct silhouette rather than a third bar color.

### 2.6 The color-only audit — every categorical encoding, verdict and remedy

Sweep of every `Success/Danger/Warning/Info` brush use in `Mainguard.App.Shell/Views/*` and `Controls/*` that accompanies a state flag. "Pass" means a non-color channel already carries the meaning (E1).

| # | Surface | States | Non-color channel today | Verdict | Remedy |
|---|---|---|---|---|---|
| 1 | Signing badge — `CommitTimelineView.axaml:248–252` | verified / untrusted / bad | in-flight: same silhouette, ~5-unit cutouts at 13 px | **Defect** | §2.3 triad + 12 px + reserved holder |
| 2 | Finding-group severity dot — `PreCommitFindingsView.axaml:73–77` | blocker / warning / info | none — identical 8 px circles | **Defect** | §2.4 triad |
| 3 | Validation-issue dot — `CommitComposerView.axaml:147–152` | error / advisory | none — identical 8 px circles | **Defect** | §2.4 triad |
| 4 | Diff change bars — `DiffViewerView.axaml.cs` `DiffMarginRenderer` | added / modified | none — identical 4 px bars | **Defect** | §2.5 solid vs hollow |
| 5 | Staged vs unstaged file rows — `StagingPanelView.axaml:108–110` vs `149–151` | staged / unstaged | section headers + separate lists carry the state | **Misuse**, not color-only: `SuccessBrush`/`DangerBrush` tint file-type glyphs and filenames as decoration — an unstaged file is not a danger (DESIGN.md §2 semantic roles; V-2) | file-type glyph → `TextMuted`, unstaged filename → `TextPrimary`, in both lists |
| 6 | File-type glyph in commit details — `CommitTimelineView.axaml:303` | (none — decoration) | n/a | **Misuse** — `SuccessBrush` with no success anywhere | → `TextMuted` |
| 7 | Check-state glyphs — `ChecksWindow.axaml:71–74`, `CommitTimelineView.axaml:323–328`, `ChecksViewModel.cs:257` | success / failure / pending / neutral | distinct text glyphs ✓ ✕ • – | **Pass** (E1) | optional consistency polish: migrate font glyphs to `PathIcon`s (`CheckmarkIcon`, `DismissIcon`, a `PendingIcon` ring-with-half-disc: `M12,2C6.48,2 2,6.48 2,12C2,17.52 6.48,22 12,22C17.52,22 22,17.52 22,12C22,6.48 17.52,2 12,2M12,4C16.42,4 20,7.58 20,12C20,16.42 16.42,20 12,20C7.58,20 4,16.42 4,12C4,7.58 7.58,4 12,4M12,6.5C15.04,6.5 17.5,8.96 17.5,12C17.5,15.04 15.04,17.5 12,17.5V6.5Z`) so weight stops depending on the platform font |
| 8 | Ref chips — `App.axaml:215–247`, `CommitTimelineView.axaml:237` | branch / tag / HEAD | tag carries `TagIcon`; HEAD is a fill-polarity inversion (solid `AccentBrush` + `OnAccent` text vs tint + accent text) — luminance, not hue | **Pass** (E1: polarity survives grayscale) | none |
| 9 | Conflict gutter accept/reject — `ConflictResolverWindow.axaml.cs:600–616` | accept / reject; chosen / available | distinct glyphs `»`/`«` vs `✕`; the chosen glyph gains a pill plate behind it | **Pass** (E1) | note for Part 3: the pill is a raw `Colors.Black 0.30` — a token gate, not an encoding gate |
| 10 | Conflict band fills — `ConflictResolverWindow.axaml.cs:451/579` | accepted / pending conflict | paired with row 9's glyph-and-pill state on the same band | **Pass** (redundant channel exists) | none |
| 11 | Blame age gutter — `BlameView.axaml.cs:159–160` (`BlameAgeNew/Old`) | continuous age, not categorical | every row prints `author · shortSha · relative-date` beside the heat bar | **Pass** (quantitative ramp + text) | Part 3 gates the ramp's lightness monotonicity per theme |
| 12 | Status pills — `LfsWindow.axaml:88–93`, `SubmodulesWindow.axaml:39–52`, `WorktreeWindow.axaml:37`, `AccountsWindow.axaml:34–39`, `PullRequestsWindow.axaml:165–170/213–215`, `ReleasesWindow.axaml:106` | various | every pill contains its label text | **Pass** (E4 exemplars) | none |
| 13 | Notification kind icons — `NotificationsWindow.axaml:87–89` | PR / issue / release | distinct glyphs (`PullRequestIcon`/`IssueIcon`/`TagIcon`) + tooltips | **Pass** (E1) | none (the GitHub-convention colors are decorative, not load-bearing) |
| 14 | `ErrorMessage` texts in `DangerBrush` — ~12 views (`CreateBranchDialog:16`, `ChecksWindow:107`, `ReflogWindow:76`, …) | error present / absent | the text *is* the meaning | **Pass** | none |
| 15 | Commit-graph lanes and dots — `CommitGraphCanvas.cs:138–141` | topology (identity), not status | n/a — Part 1's G1–G5 exist precisely so lanes can never be read as status | **Pass by design** | Part 1 |

Three genuine defects beyond the signature badge (rows 2–4), two semantic-brush misuses (rows 5–6), and a healthy pattern everywhere pills carry their own text — E4 is codifying existing good behavior, not inventing it.

### 2.7 Deltas for the in-flight fix to apply

1. **`App.axaml`** — replace the `SignatureWarningIcon` and `SignatureBadIcon` geometries with §2.3's values (verified is unchanged); update the T-15 comment to point here. Add `SeverityBlockerIcon` and `SeverityInfoIcon` from §2.4 (and optionally `PendingIcon` from audit row 7). No theme file changes — icons are theme-independent.
2. **`CommitTimelineView.axaml:248–252`** — badge `Panel` and `PathIcon`s `13` → `12`; keep the holder always-present and toggle glyph visibility on `HasSignatureBadge` (E3 / PolishSpec §1's reflow test: message left edge identical across signed and unsigned rows).
3. **`PreCommitFindingsView.axaml:73–77`** — the three dots → three 10×10 `PathIcon`s (`SeverityBlockerIcon`/`WarningIcon`/`SeverityInfoIcon`), same flags, same brushes.
4. **`CommitComposerView.axaml:147–152`** — the two dots → `SeverityBlockerIcon` (`IsError`) / `WarningIcon`, 10×10, fixed-width holder.
5. **`DiffViewerView.axaml.cs` `DiffMarginRenderer`** — modified-line bars render hollow (1 px `AccentBrush` pen, no fill); added stays solid.
6. **`StagingPanelView.axaml:108/149–150` and `CommitTimelineView.axaml:303`** — file-type glyphs → `TextMuted`; the unstaged filename → `TextPrimary`.
7. **Shipping gate**: PolishSpec §7's render harness, plus one grayscale capture of the signing badge and severity rows — the E1 gate is "each state pair distinguishable in the desaturated frame at shipping size," in Midnight *and* Daylight at minimum.

### 2.8 Verification record

- **E1** — each state family re-checked in grayscale terms: signature = solid/hollow/fractured (mass 85 % / 25 % / 80 %-with-breached-contour at 12 px); severity = octagon/triangle/circle silhouettes; diff bars = solid/hollow; checks = four distinct glyphs; all pills = text. Zero categorical pairs remain brush-only after §2.7.
- **E2** — the three signature geometries share the byte-identical outer base path `M12,1L3,5V11C3,16.55 6.84,21.74 12,23C17.16,21.74 21,16.55 21,11V5L12,1`; the severity triad shares the filled-primitive-with-knocked-out-mark construction; no fact borrows another's base.
- **E3** — the badge holder, severity panels, and diff-gutter slot are all fixed-width with content-only toggling; no state change moves layout.
- **E4** — audit row 12 confirms every status pill carries text; no remedy introduces a bare dot.
- **Geometry integrity** — all paths are single-brush, even-odd-safe (nesting analyzed in §2.3/§2.4), self-intersection-free, within the 24×24 box with ≥1-unit margins, and sized on the AGENTS.md icon scale (12 badge, 10 severity adornments). No new color tokens; no raw colors introduced; the one raw color *found* (`Colors.Black` pill, audit row 9) is handed to Part 3.

## Part 3 — Accessibility across the five themes

Part 3 is the WCAG 2.1 AA contract for every color pairing the app actually renders: the text roles, the diff add/remove surfaces (ThemeRefinement §4.2 was the proposal of record — this part gates it), the badges and pills, and the two items Part 2 handed over (the raw `Colors.Black 0.30` pill plate, audit row 9; the blame age ramp, audit row 11) — plus the BlameView live-recolor decision as the design pattern of record. Part 1's lane values are **ratified by independent recomputation, not redone** (§3.10); the new ground is everything else.

**Method** — identical engines to Part 1 so every number is comparable: WCAG 2.1 relative-luminance contrast; straight-alpha compositing in sRGB (how Avalonia blends, so every translucent token is gated as the *composite* the eye receives); Viénot 1999 deuteranopia with HSL-lightness readout (deutL); CIEDE2000. Deuteranopia is the design-time CVD gate, matching Parts 1–2; protanopia/tritanopia go to the ThemeRefinement §7 harness spot-check. Nothing below is estimated.

**Text-size stance.** Mainguard's type ramp tops out at 16–18 px titles; status pills run 9–10 px bold (`SubmodulesWindow.axaml:39–52`, `PullRequestsWindow.axaml:165–170`). Nothing qualifies for WCAG's large-text 3:1 relaxation, so **every text pairing owes 4.5:1** — the large-text exemption is deliberately unused. Non-text marks (icon fills, focus rings, graph strokes, the ✓/✕/• check glyphs of audit row 7) owe the 1.4.11 3:1 floor.

### 3.1 The defects, verified

Computed against the working tree (current `Themes/*.axaml` plus the §4.2 diff proposals where they are the record). Dark themes are healthy: Midnight, Command Deck, Atelier, and Aurora pass every text, badge, and fill gate outright (minima in §3.11). The failures cluster in four places:

1. **Day Watch's semantic and accent tokens systematically fail as text and as fills.** ThemeRefinement §1 asserted "surfaces, text, accent, and the semantic fills already clear WCAG AA for their use" and scoped them out; the computation disproves that for the one light theme, and this part supersedes that scoping. Measured, worst pairings:

   | Surface (real, shipping) | Pairing | Measured | Gate |
   |---|---|---|---|
   | Force-push flyout item, `MainWindow.axaml:323` — 13 px `DangerBrush` text, ghost bg, hovered (`SurfaceHover`) | `#D64545` on `#E3E6EE` | **3.50** | 4.5 |
   | LFS "Downloaded" pill, `LfsWindow.axaml:88` — 10 px `OnAccent` on `SuccessBrush` | `#FFFFFF` on `#1F9D55` | **3.49** | 4.5 |
   | Submodule "Modified" pill, `SubmodulesWindow.axaml:44` — 10 px `OnAccent` on `WarningBrush` | `#FFFFFF` on `#A9770F` | **3.94** | 4.5 |
   | `Button.Accent` / HEAD ref chip — `OnAccent` on `AccentBrush` | `#FFFFFF` on `#6467E8` | **4.26** | 4.5 |
   | PR "Approved" verdict badge, `PullRequestsWindow.axaml:166` — 10 px bold `SuccessBrush` text | `#1F9D55` on `#F7F8FB` / `#FFFFFF` | **3.29 / 3.49** | 4.5 |
   | Accent links (`DiffViewerView.axaml:100` HeaderText, blame popover PR numbers) on `SurfacePanel` | `#6467E8` on `#F7F8FB` | **4.26** | 4.5 |
   | Signature/severity icons on a selected row (`AccentSelection` over `SurfacePanel`) | `#1F9D55` on `#E1E2F8` | **2.73** | 3.0 |
   | Blocker banner, `PreCommitFindingsView.axaml:44` — `DangerBrush` text on the §4.2 `DiffRemovedBg` | `#D64545` on `#F5C6CF` | **2.89** | 4.5 |

   The tell: Daylight's *hover* variants all pass (white on `SuccessHover #178246` = 4.86, on `DangerHover #C43A3A` = 5.23, on `AccentHover #5457DE` = 5.53) — the theme's rest states are one step too light, and its own hover states prove the fix. Remedy: §3.4.

2. **`TextMuted` fails on selected rows in three dark themes.** The selection tint (`AccentSelection`, 15 % alpha accent over `SurfacePanel` — DESIGN.md §5) lightens the row just enough that muted metadata (commit author/date in `CommitTimelineView`, file paths in `StagingPanelView`) drops below 4.5: Command Deck **4.12**, Atelier **4.33**, Aurora **4.28** (Midnight 4.69 and Daylight 4.54 pass — their accents are dimmer/cooler). Remedy: §3.5.

3. **The §4.2 diff proposals miss their own gates in places.** Atelier add-vs-remove separation **1.14** (its own table admits it; gate ≥ 1.15); Aurora **1.14**, and Aurora's word-emphasis composite puts `TextPrimary` at **4.47**; the promised deuteranopic hue split between added and removed does not actually survive projection in Midnight, Daylight, or Atelier (both project to the same ~60° family); and the blocker banner (defect 1's last row) shows `DiffRemovedBg` being used as a UI panel surface it can never satisfy. Remedy: §3.6.

4. **The blame age ramp and the conflict pill plate** — the two handed items — fail as reported: `BlameAgeNew` is *hex-identical* to `WarningBrush` in Midnight/Command Deck/Aurora (`#E3B341`/`#FBBF24`/`#FACC6B` — a recently edited file renders a gutter column of literal warning color, the Part 1 defect with a different token) and sits ΔE00 4.4 from it in Atelier; every theme's `BlameAgeOld` endpoint is sub-perceptible against the editor (1.71–1.93 vs `SurfaceDeep`); Daylight's whole ramp spans only 1.79:1 end to end. The `Colors.Black 0.30` pill plate *helps* on dark themes (chosen-glyph contrast rises ~1.2×) but **inverts on Daylight**: it drops the chosen Success/Danger glyphs to **1.13–1.77** against the plate — the accessibility of the conflict resolver's most important state is destroyed by the theme the plate never considered. Remedies: §3.7, §3.8.

One pattern ratified as-is: the in-flight `BlameGutterMargin` `ThemeChanged` subscription (`BlameView.axaml.cs:137–149`) is correct — §3.9 records it as the pattern of record and extends it to the two renderers that still owe it.

### 3.2 The accessibility gates (named rules)

These extend Part 1's G1–G5 and Part 2's E1–E4 with six *contrast-and-liveness* gates. They are design-time gates; the shipping gate remains ThemeRefinement §7's unit test + render harnesses, whose thresholds should be updated to encode exactly these rules.

**A1 — The Text-Contrast Gate.** Every text role clears **4.5:1 on every surface it rests on** — and "rests on" includes the transient and composite surfaces: `SurfaceHover` (any text inside a hover-fillable control — a ghost button's label is read *while hovered*), and the `AccentSelection`-over-`SurfacePanel` composite (selected rows keep their metadata readable). The role map that owes this gate: `TextPrimary` and `TextMuted` on all six opaque surfaces + the selection composite; `AccentBrush` as text (links, `HeaderText`, `Button.Pill` labels) on `SurfaceDeep`/`Panel`/`Card`/`Hover`/selection; `SuccessBrush`/`DangerBrush`/`WarningBrush` as text on `Panel`/`Card`/`Hover`. `InfoBrush` is **not a text role today** (zero `Foreground` bindings in `Views/`) — its first use as text owes this gate before it ships.

**A2 — The Readable-Tint Gate.** The diff line backgrounds are *tints under content*, and the gate is on the composite: `TextPrimary` ≥ 4.5:1 over `DiffAddedBg`/`DiffRemovedBg` **and** over the word-emphasis composites (`DiffAdded/RemovedEmphasis` alpha-blended onto their line background), per theme. Corollary, from defect 3: **diff tints carry only diff content in `TextPrimary`/mono — semantic or muted UI text never sits on a diff tint.** (The one violation, the `PreCommitFindingsView:44` blocker banner, re-surfaces to `SurfaceCard`; its `DangerBrush` border + Part 2's octagon carry the severity. `DangerBrush` text on `SurfaceCard`: 6.05 / 6.21 / 6.69 / 5.36 / 5.97 across the five themes.)

**A3 — The Diff-Polarity Gate.** Add-vs-remove backgrounds hold ≥ **1.15 luminance separation** and a **deuteranopic hue split** (added projects warm ~60°, removed projects cool ~240°) in every theme, so polarity survives red-green CVD twice over — and color is still never the only channel: the gutter +/− glyphs and Part 2 §2.5's solid/hollow margin bars are the guaranteed zero-color carriers (E1).

**A4 — The Badge Gate.** `OnAccent` clears 4.5:1 on **every fill that carries it** (`Accent`, `Success`, `Danger`, `Warning`, and all hover variants — pills, toasts, filled buttons, the HEAD chip inversion). Any semantic brush used as an icon or glyph fill (signature triad, severity triad, check glyphs, diff margin bars) clears **3:1 against `SurfacePanel`, `SurfaceCard`, and the selection composite**. The focus ring (`AccentBrush` border on focused inputs) clears 3:1 against `SurfaceCard` and `SurfacePanel`.

**A5 — The Ramp Gates** (any sequential ramp; today that is `BlameAgeOld → BlameAgeNew`, lerped in sRGB by `BlameGutterMargin.LerpHeat`):
- *Signal end*: the **new** endpoint ≥ **3.2:1** vs `SurfaceDeep` (the editor the gutter sits on — recency is the signal the ramp exists to carry).
- *Recede end*: the **old** endpoint ≥ **2.0:1** vs `SurfaceDeep` — a perceptibility floor, not the 1.4.11 floor. This deliberately supersedes PolishSpec §6's "old endpoint ≥ 3:1": age is redundantly printed as text on every row (`author · shortSha · relative-date`, audit row 11), so the bar is an enhancement, not the sole carrier — and on Daylight, holding *both* endpoints to 3:1 against white provably pins them into the same luminance band and erases the ramp itself. The signal end gets the graphical floor plus margin; the quiet end gets "never vanishes."
- *Range*: endpoints ≥ **2.0:1 apart**, so the ramp has discriminable travel.
- *Monotonicity*: relative luminance **and** deutL move monotonically along the full lerp (64-sample scan, no reversal step > 0.002) — under CVD, age stays orderable by lightness alone.
- *Decoupling*: ΔE00 ≥ **10** from all four primary semantic tokens at the endpoints and ≥ **8** everywhere along the interior; no ramp endpoint is hex-identical to any token (G1 extended off the lanes).

**A6 — The Live-Recolor Gate.** Every long-lived, code-drawn visual that resolves tokens (via `TryGetResource`/`ThemeBrush.Resolve`) subscribes to `ThemeManager.ThemeChanged` with a **paired subscribe/unsubscribe on visual-tree attach/detach**, and its handler invalidates every cached token-derived artifact before repainting. Full decision: §3.9.

### 3.3 The Daylight ink retune

The remedy for defect 1 follows the theme's own header ("the Loom violet accent **deepened for contrast** on light backgrounds") one honest step further, and Part 1's smallest-move principle: **where the existing hover value already passes, the rest state deepens into that register and the hover deepens one step beyond** (Daylight hovers already darken, so the direction is established). Warning, which has no passing sibling, is rebuilt in place. `InfoBrush` needs no move (never text; ≥ 3.48 as an icon fill everywhere, including the selection composite). Hue identity holds — indigo-violet stays indigo-violet, the greens stay green — this is ink, not a new palette.

| Token | Was | Now | as text: Panel / Card / Hover | white (`OnAccent`) on fill | min ΔE00 to a lane |
|---|---|---|---|---|---|
| `AccentBrush` | `#6467E8` | **`#4B4ED8`** | 5.85 / 6.21 / 4.98 | 6.21 | 6.9 (Lane1 — warp-thread kinship, sanctioned by G5; accent is not a status role) |
| `AccentHover` | `#5457DE` | **`#4144C8`** | (fill only) | 7.28 | — |
| `AccentSelection` | `#266467E8` | **`#264B4ED8`** | composite `#DDDFF6`: `TextPrimary` 12.16 · `TextMuted` 4.54 · accent pill text 4.72 | — | — |
| `SuccessBrush` | `#1F9D55` | **`#0E6B37`** | 6.22 / 6.61 / 5.29 | 6.61 | 15.9 (Lane3) |
| `SuccessHover` | `#178246` | **`#0B5A2C`** | (fill only) | 8.36 | 14.4 (Lane3, hover gate ≥ 8) |
| `DangerBrush` | `#D64545` | **`#B33030`** | 5.85 / 6.21 / 4.97 | 6.21 | 20.2 (Lane4) |
| `DangerHover` | `#C43A3A` | **`#9C2626`** | (fill only) | 7.72 | 18.4 (Lane4, hover gate) |
| `WarningBrush` | `#A9770F` | **`#7E5D07`** | 5.72 / 6.08 / 4.87 | 6.08 | 13.0 (Lane4 — the tight one; verified ≥ 10) |
| `InfoBrush` | `#0B78CE` | *(unchanged)* | not a text role (A1) | not an `OnAccent` fill | 12.6 (Lane5) |

Mutual distinctness of the new set: min pairwise ΔE00 **17.8** (Accent↔Info); every other pair ≥ 30. Every defect-1 row now passes: the force-push item 4.97, the LFS pill 6.61, the Modified pill 6.08, `Button.Accent` 6.21, the Approved badge 6.22, accent links 5.85, icons on selection ≥ 3.48.

**Part 1 re-verification under the new semantics** (G2 is measured against the theme's semantic tokens, so Daylight's lanes were re-gated; all five Part 1 values stand unchanged): Lane1 23.3 / Lane2 21.1 / Lane3 15.9 / Lane4 13.0 / Lane5 12.6 min ΔE00 to a primary; 40.4 / 21.6 / 14.4 / 18.4 / 42.4 to a hover. Contrast and staircase are untouched (lanes didn't move).

**Ripple tokens** (derived values that embed the semantic hexes; same file, same change): `DiffAddedEmphasis` → `#660E6B37`, `DiffRemovedEmphasis` → `#66B33030`, `DiffWhitespaceMarker` → `#557E5D07` (its marker composite on the added tint *improves*, 1.44 → 1.60).

### 3.4 TextMuted on selected rows — three quiet lifts

Defect 2's remedy is the minimal lightening of `TextMuted` that clears the selection composite with margin — one move that also widens the thin hover-surface numbers (4.54–4.71 today):

| Theme | Was | Now | vs selection composite | vs `SurfaceHover` | vs `SurfacePanel` |
|---|---|---|---|---|---|
| Command Deck | `#7C8A94` | **`#8794A0`** | 4.12 → **4.71** | 4.54 → 5.19 | 5.34 → 6.11 |
| Atelier | `#9C9284` | **`#A29888`** | 4.33 → **4.66** | 4.71 → 5.07 | 5.66 → 6.09 |
| Aurora | `#8D93B8` | **`#959BC0`** | 4.28 → **4.73** | 4.58 → 5.06 | 5.76 → 6.36 |

Midnight (`#8A93A6`, 4.69) and Daylight (`#5C6470`, 4.54 on the *new* composite) pass unchanged and are ratified — Daylight's margin is thin and is flagged to the §7 unit test as a regression tripwire, not retuned (the same edge Part 1 accepted at Aurora Lane5's 3.04). Muted stays muted: each lift is ≤ 5 % lightness, well inside the role's register.

### 3.5 Diff add/remove — ThemeRefinement §4.2, gated

§4.2 is ratified where it passes and corrected where it misses its own targets. Two theme-level emphasis alphas also move, because A2 gates the *composite* and two of them shipped at or past the edge (Command Deck 4.52, Aurora 4.47):

| Theme | `DiffAddedBg` | `DiffRemovedBg` | sep (≥1.15) | `TextPrimary` on add/rem | on emphasis add/rem | deutan add/rem |
|---|---|---|---|---|---|---|
| Midnight | `#123726` *(§4.2 ratified)* | **`#3A162B`** *(was `#3A1622` — B-channel lift restores the hue split)* | 1.20 | 10.79 / 12.99 | 5.30 / 6.37 | 60° / **240°** |
| Daylight | `#D9F2E1` *(ratified)* | **`#F5C6DA`** *(was `#F5C6CF`, same reason)* | 1.27 | 13.52 / 10.65 | 7.41 / 6.16 | 60° / **240°** |
| Command Deck | `#0C3226` *(ratified)* | `#2E1220` *(ratified)* | 1.23 | 10.80 / 13.24 | **4.91** *(emphasis alpha `0x66`→`0x5C`: `#5C34D399`)* / 6.28 | 60° / 240° |
| Atelier | **`#1F3823`** *(was `#1D3420`, sep 1.14 → 1.18)* | **`#3C1C2B`** *(was `#3C1C1C`; hue split)* | 1.18 | 10.35 / 12.24 | 4.75 / 6.34 | 60° / **240°** |
| Aurora | **`#123B2B`** *(was `#0E3325`, sep 1.14 → 1.27)* | `#3A162B` *(ratified)* | 1.27 | 10.12 / 12.84 | **4.97** *(emphasis alpha → `#524ADE80`)* / 6.18 | 60° / 240° |

With the five corrections, **all five themes now hold both A3 channels**: ≥ 1.18 luminance separation *and* the warm/cool deuteranopic hue split §4.2 promised but delivered in only two themes. Emphasis composites stay clearly visible against their line tints (1.72–2.39); whitespace markers read at 1.51–2.26. The blocker banner re-surfaces per A2 (`DiffRemovedBg` → `SurfaceCard` at `PreCommitFindingsView.axaml:44`; border, icon, and copy carry the severity — V-2's "severity rides the role" needs no red wash). The `ConflictResolverWindow` gutters resolve `DiffAdded/RemovedBg` for their band fills (`.axaml.cs:446–447, 582–583`) and inherit these values automatically — their fallback literals must move with them (§3.9).

### 3.6 The blame age ramp — heat without a warning

Audit row 11's handoff, plus defect 4. The design keeps the ramp's metaphor — **old settles toward the surface, recent glows ember** — but decouples the ember from `WarningBrush` (the glow moves from brass-amber to a distinctly redder ember *orange*, the same family Part 1 chose for Midnight's Lane4, hotter and truer as "heat" than brass ever was) and lifts every old endpoint to the perceptibility floor. On Daylight the polarity flips honestly: the old end is the *light* end (receding into paper), the new end is a deep umber-ember that pops against white — salience follows the theme, monotonicity holds in both directions.

| Theme | `BlameAgeOld` | `BlameAgeNew` | old / new vs `SurfaceDeep` | range | endpoint min ΔE00 (interior) | deutL travel |
|---|---|---|---|---|---|---|
| Midnight | `#3B4252` → **`#414A5E`** | `#E3B341` → **`#F08A2E`** | 2.19 / 7.76 | 3.54 | 17.3 (17.3, Warning) | 0.325 → 0.406 |
| Daylight | `#B9C0CC` → **`#A9B1C1`** | `#C77D2E` → **`#AC6112`** | 2.15 / 4.70 | 2.18 | 13.6 (13.3, Warning) | 0.721 → 0.245 |
| Command Deck | `#2E3A40` → **`#3B4A52`** | `#FBBF24` → **`#FF9430`** | 2.17 / 9.05 | 4.17 | 17.4 (17.4, Warning) | 0.299 → 0.430 |
| Atelier | `#4A4238` → **`#4F473C`** | `#D9A441` → **`#E5822E`** | 2.08 / 6.85 | 3.29 | 18.8 (18.8, Warning) | 0.261 → 0.391 |
| Aurora | `#33395C` → **`#414767`** | `#FACC6B` → **`#FF9E45`** | 2.12 / 9.36 | 4.41 | 16.1 (16.1, Warning) | 0.338 → 0.496 |

All ten ramps scan monotone in luminance and deutL (worst reversal step 0.0016, inside the 0.002 tolerance — sRGB lerp quantization, not a design reversal); zero hex identities remain against any token. Atelier's near-collision (4.4) resolves to 18.8; the nearest semantic anywhere on any ramp is 13.3.

**One geometry decision travels with the values:** the alternating commit-block shade currently paints the full gutter width (`BlameView.axaml.cs:180`), putting the heat bar on `SurfaceHover` on every other block — where Midnight's old bar reads at **1.61**, below the floor the endpoints were just tuned to. Reserve the bar's column: the dim shade starts at `x = HeatBarWidth`, so the 4 px bar always sits on `SurfaceDeep`, the surface A5 gates against. (The same reserve-the-column instinct as E3 and the DESIGN.md §5 selection rail.)

### 3.7 The conflict pill plate — `GlyphPlate`, a token with a polarity

Audit row 9's handoff. The raw `new SolidColorBrush(Colors.Black, 0.30)` in `ConflictResolverWindow.axaml.cs:600–616` becomes a theme token, because "darken what's behind me" is only correct on dark themes — measured on Daylight it *inverts* (defect 4). The role: **a translucent seating plate that pushes a variable tinted band away from the glyph painted on it** — so its polarity belongs to the theme, which is precisely what a token is:

```xml
<!-- Seating plate for code-drawn state glyphs on tinted fills (DesignSystem.md Part 3 §3.7).
     Dark themes deepen the band; Daylight lifts it toward paper. -->
<SolidColorBrush x:Key="GlyphPlate" Color="#4D000000"/>   <!-- Midnight, Command Deck, Atelier, Aurora -->
<SolidColorBrush x:Key="GlyphPlate" Color="#66FFFFFF"/>   <!-- Day Watch -->
```

Chosen-glyph contrast on the plate composite, worst band per theme: Midnight **5.79**, Command Deck **6.16**, Atelier **5.19**, Aurora **5.79** (all *improved* over the raw band, ratifying the dark-theme behavior the literal accidentally had) — and Daylight recovers from 1.13–1.77 to **4.89–5.97**. Two shape corrections ride along: the pill's corner radius moves from the off-scale `4` to **999** (it is a pill; the scale's own capsule radius), and the pill gains a **1 px stroke in the glyph's own brush** — a ring is geometry, so the *chosen* state now survives grayscale outright (ring contrast tracks glyph-on-band contrast, ≥ 4.13 at its worst: Daylight Danger on the removed band), reinforcing Part 2 row 9's E1 verdict at every theme polarity. Fallback literal in the resolver: `#4D000000` (the Midnight value, per the fallback-mirror rule below).

### 3.8 The BlameView live-recolor decision (A6)

The confirmed bug: `BlameGutterMargin` repainted only on `VisualLinesChanged`, so a theme switch left the gutter in the old theme's heat and text colors until the next scroll. The working tree already carries a fix; **this section ratifies it as the pattern of record** and specifies its semantics so the fixers keep, not rediscover, them:

1. **Subscription lifetime = visual-tree lifetime.** Subscribe in `OnAttachedToVisualTree`, unsubscribe in `OnDetachedFromVisualTree` — exactly `CommitGraphCanvas.cs:43–53`, exactly the in-flight `BlameView.axaml.cs:137–147`. `ThemeManager.ThemeChanged` is a **static event**: an unpaired subscription roots the margin (and the whole editor tree behind it) for the process lifetime, and re-subscribing per `OnTextViewChanged` (as PolishSpec §6 sketched — superseded here) can double-subscribe when AvaloniaEdit re-parents the margin. Attach/detach is balanced by construction.
2. **Handler = invalidate caches, then repaint.** The gutter resolves every brush per-`Render`, so `InvalidateVisual()` alone is complete — but the rule is stated cache-first because `CommitGraphCanvas` *does* cache (`_laneColorsCache = null` before its invalidate, `.cs:55–59`). If the gutter ever gains a resolved-brush cache, its handler inherits the clear. Never `InvalidateMeasure()` here: a theme switch changes no geometry (M-4 — state, not spectacle; no layout response to a color event).
3. **Fallback mirror rule.** A resolver's literal fallback is a silent second copy of a token; it must equal the **current Midnight spec value** or it reintroduces the retired color on any resolution miss (Part 1 §1.6's lesson). Deltas owed by this part: `BlameView.axaml.cs:159–160` fallbacks `#E3B341`→`#F08A2E` and `#3B4252`→`#414A5E`; `ConflictResolverWindow` `ThemeBrush.Resolve` fallbacks `#11271B`→`#123726` and `#33191E`→`#3A162B` (lines 446–447 and 582–583); the new `GlyphPlate` resolve falls back to `#4D000000`.
4. **The gate has two more debtors.** Sweep of every code-drawn, token-resolving visual: `CommitGraphCanvas` ✓, `IntraLineDiffTextBlock` ✓ (`.cs:69–76`), `BlameGutterMargin` ✓ in-flight — and **`MergeGutter` (`ConflictResolverWindow.axaml.cs:530`) and `DiffMarginRenderer` (`DiffViewerView.axaml.cs:161`) do not subscribe**: both resolve tokens at draw time and go stale on a theme switch exactly as the blame gutter did (the merge gutter is the *conflict resolver* — the surface DESIGN.md's safety promise lives on). `MergeGutter` is a `Control`: apply pattern 1 verbatim. `DiffMarginRenderer` is an `IBackgroundRenderer` with no visual tree of its own: its owning view subscribes and calls `TextView.InvalidateLayer(KnownLayer.Background)`, unsubscribing when the editor detaches.

### 3.9 Deltas for the in-flight fixes to apply

Theme files (every token below already exists in all five files except `GlyphPlate`, which is new and lands in **all five** per N-5):

| File | Token | Working tree | This spec |
|---|---|---|---|
| `Themes/DaylightLoom.axaml` | `AccentBrush` / `AccentHover` / `AccentSelection` | `#6467E8` / `#5457DE` / `#266467E8` | `#4B4ED8` / `#4144C8` / `#264B4ED8` |
| `Themes/DaylightLoom.axaml` | `SuccessBrush` / `SuccessHover` | `#1F9D55` / `#178246` | `#0E6B37` / `#0B5A2C` |
| `Themes/DaylightLoom.axaml` | `DangerBrush` / `DangerHover` | `#D64545` / `#C43A3A` | `#B33030` / `#9C2626` |
| `Themes/DaylightLoom.axaml` | `WarningBrush` | `#A9770F` | `#7E5D07` |
| `Themes/DaylightLoom.axaml` | `DiffAddedEmphasis` / `DiffRemovedEmphasis` / `DiffWhitespaceMarker` | `#661F9D55` / `#66D64545` / `#55A9770F` | `#660E6B37` / `#66B33030` / `#557E5D07` |
| `Themes/DaylightLoom.axaml` | `DiffAddedBg` / `DiffRemovedBg` | `#DDF3E4` / `#FBE2E2` | `#D9F2E1` / `#F5C6DA` |
| `Themes/DaylightLoom.axaml` | `BlameAgeNew` / `BlameAgeOld` | `#C77D2E` / `#B9C0CC` | `#AC6112` / `#A9B1C1` |
| `Themes/MidnightLoom.axaml` | `DiffAddedBg` / `DiffRemovedBg` | `#11271B` / `#33191E` | `#123726` / `#3A162B` |
| `Themes/MidnightLoom.axaml` | `BlameAgeNew` / `BlameAgeOld` | `#E3B341` / `#3B4252` | `#F08A2E` / `#414A5E` |
| `Themes/CommandDeck.axaml` | `TextMuted` | `#7C8A94` | `#8794A0` |
| `Themes/CommandDeck.axaml` | `DiffAddedBg` / `DiffRemovedBg` / `DiffAddedEmphasis` | `#0D251F` / `#2A161B` / `#6634D399` | `#0C3226` / `#2E1220` / `#5C34D399` |
| `Themes/CommandDeck.axaml` | `BlameAgeNew` / `BlameAgeOld` | `#FBBF24` / `#2E3A40` | `#FF9430` / `#3B4A52` |
| `Themes/Atelier.axaml` | `TextMuted` | `#9C9284` | `#A29888` |
| `Themes/Atelier.axaml` | `DiffAddedBg` / `DiffRemovedBg` | `#1E2A1A` / `#33201D` | `#1F3823` / `#3C1C2B` |
| `Themes/Atelier.axaml` | `BlameAgeNew` / `BlameAgeOld` | `#D9A441` / `#4A4238` | `#E5822E` / `#4F473C` |
| `Themes/LoomAurora.axaml` | `TextMuted` | `#8D93B8` | `#959BC0` |
| `Themes/LoomAurora.axaml` | `DiffAddedBg` / `DiffRemovedBg` / `DiffAddedEmphasis` | `#10291F` / `#351A26` / `#664ADE80` | `#123B2B` / `#3A162B` / `#524ADE80` |
| `Themes/LoomAurora.axaml` | `BlameAgeNew` / `BlameAgeOld` | `#FACC6B` / `#33395C` | `#FF9E45` / `#414767` |
| **all five** `Themes/*.axaml` | `GlyphPlate` *(new)* | — | `#4D000000` (dark four) · `#66FFFFFF` (Daylight) |

View/code-behind decisions (design, for the code fix to adopt — no reflow, no new geometry beyond §3.7's pill):
1. `PreCommitFindingsView.axaml:44` — blocker banner `Background` `DiffRemovedBg` → `SurfaceCard` (A2).
2. `ConflictResolverWindow.axaml.cs:600–616` — the raw black pill → `GlyphPlate` resolve (fallback `#4D000000`), radius 999, 1 px glyph-brush stroke (§3.7).
3. `BlameView.axaml.cs:180` — dim shade starts at `x = HeatBarWidth` (§3.6); fallbacks at `:159–160` updated (§3.8-3).
4. `ConflictResolverWindow.axaml.cs:446–447/582–583` — diff-token fallbacks updated (§3.8-3).
5. `MergeGutter` + `DiffMarginRenderer` gain the A6 subscription (§3.8-4).
6. ThemeRefinement §7's unit-test gate adopts A1–A5 as its assertions (the role map in A1 is the pairing list), adding the two flagged thin margins (Daylight `TextMuted`-on-selection 4.54; Atelier diff sep 1.18) as explicit regression rows.

### 3.10 Part 1 ratification record

All 25 lane values recomputed from scratch with this part's engines: every figure in Part 1's tables reproduces (spot-identical: Midnight Lane4 5.96 vs Panel, ΔE00 18.5 from Warning; Aurora Lane5 3.29/3.04; Atelier Lane4 3.25; staircase minima 0.074 / 0.084 / 0.070 / 0.075 / 0.075). Daylight's lanes were additionally re-gated against §3.3's new semantics and pass with room (§3.3). **Part 1 is ratified unchanged.**

### 3.11 Verification record

- **A1** — `TextPrimary` ≥ 10.44 everywhere (min: Aurora on selection); `TextMuted` ≥ 4.54 everywhere after §3.4 (min: Daylight on selection, flagged); accent-as-text ≥ 4.72 (min: Daylight on selection); semantic-as-text ≥ 4.77 (min: Atelier Danger on Hover). Zero text pairings below 4.5 remain in any theme.
- **A2** — `TextPrimary` over every tint and emphasis composite ≥ 4.52 (min: Command Deck pre-alpha-fix 4.52 → 4.91 after; global min now 4.75, Atelier add-emphasis). No UI text remains on a diff tint.
- **A3** — separation ≥ 1.18 (Atelier, flagged) and warm/cool deutan split in **5/5 themes** (was 2/5 under §4.2 as proposed).
- **A4** — `OnAccent` on fills ≥ 6.03 (min: Atelier Danger); semantic icon fills ≥ 3.48 vs their worst surface (min: Daylight Info on selection); focus ring ≥ 5.66 (min: Midnight vs Card; Daylight rises 4.26 → 5.85 with §3.3).
- **A5** — old endpoints 2.08–2.19; new endpoints 4.70–9.36; ranges 2.18–4.41; all ten monotone scans pass; min ΔE00 to any semantic anywhere on any ramp 13.3; hex identities 0 (was 3).
- **A6** — compliant: `CommitGraphCanvas`, `IntraLineDiffTextBlock`, `BlameGutterMargin` (in-flight, ratified). Owed and specified: `MergeGutter`, `DiffMarginRenderer`. Fallback mirrors: 5 literals updated, 1 added.
- **Sanctioned aliases** (excluded from the hex-identity sweep by design, per DESIGN.md §2's own definitions): `OnAccent` == `SurfaceDeep` (Midnight, Command Deck, Aurora), == `SurfaceWindow` (Atelier), == `SurfaceDeep`/`SurfaceCard` == `#FFFFFF` (Daylight).

## Part 4 — Motion & delight vocabulary

Part 4 is the decision layer for every timed visual change in the app: *which* moments earn motion, *what* motion they get, and the grammar that lets a future surface pick motion by rule instead of taste. It completes the document.

**Relationship to the existing motion work.** [`docs/creative/MotionPlaybook.md`](../creative/MotionPlaybook.md) remains the implementation-brief companion — its global table (rows `G-1`–`G-10`) and twelve storyboards are **ratified here as the mechanism layer** (the `DoubleTransition`-on-a-pre-colored-layer pattern, the ghost-flash rule, the never-animate list). This part sits above it: it names the grammar the Playbook's briefs instantiate, adds the storyboards the Playbook lacks (a conflict fully resolved, a hazard blocked, a push landing), and where the two ever disagree, this part wins. The Bible's motion rules ([`M-1`–`M-7`](../creative/Mainguard_Voice_And_Delight_Bible.md)) govern both. *Citation disambiguation:* the Playbook's rows are hyphenated `G-#`; Part 1's lane gates are unhyphenated `G#` (the Part 2 `E#`/`E-#` convention). Part 4's gates use a fresh letter — `D#` — so no citation is ambiguous, and the primitives below have *names* precisely so future docs can cite a word (`Settle`) instead of a fourth letter-number series.

**The budget, restated once.** Motion clarifies state (PRODUCT.md Design Principle 3): **opacity and brush only, 120–150 ms, no bounce, no property that triggers layout** (`M-3`). Lane B's SurfaceDesigns and FeatureDesigns already committed every surface to this family; Part 4 ratifies their usage and systematizes it.

### 4.1 The earned-moment ledger

The design stance, inherited from `M-1`/`M-2`: **stillness is the default; motion is a scarce signal spent only where a state change completes, blocks, or redirects the user's task.** The ledger below is exhaustive for the shipped client — a moment not on it is silent, and a new surface adds its moment *here* before adding an animation (gate D1).

| # | Moment | Real surface | Verdict | Motion (primitive, §4.3) |
|---|---|---|---|---|
| 1 | **Merge or rebase lands** | `RepoDashboardView` `Border.toast` pill via `ShowNotification` (`RepoDashboardViewModel.cs:210`) | **Earns** — the emotional peak (`M-1`) | Settle-140 in → 3 s hold → Release-130 (§4.4.1) |
| 2 | **A conflict file fully resolves** | `ConflictResolverWindow` toolbar `StatusText` + the `MarkResolvedCommand` `Button.Accent` | **Earns** — the weave closes (§4.4.2) | Exchange-140 on the status line; the global Shift lights the accent |
| 3 | **A clone completes** | `CloneDashboardView` progress overlay → workspace → pill | **Earns** — a four-beat sequence, each beat one primitive (§4.4.3) | Draw → Release-130 → Still → Settle-140 |
| 4 | **A push succeeds** | `Border.toast` pill, `Pushed feature to origin.` (Bible V-7's own example) | **Earns** — quietly (§4.4.4) | Settle-140 / hold / Release-130 |
| 5 | **A hazard blocked** | `PreCommitFindingsView` blocker banner; force-with-lease refusal; `UndoBlockedException`; the bisect dirty-tree refusal (FeatureDesigns §1.3.3) | **Earns the deliberate zero** (§4.4.5) | Still — the brake is instant |
| 6 | **A bisect verdict narrows the range** | `CommitTimelineView` weave rows (FeatureDesigns §1.8, "the narrowing weave") | **Earns** — delight as comprehension | Recede-130 to the 0.45 rest stop; the culprit card lands with one Settle-140 |
| 7 | **Clean tree / all clear reached** | staging panel affirmation (`ES-4`); the multi-repo attention rail's `Nothing needs attention` (FeatureDesigns §3.7, "the loom at rest") | **Earns** — a single fade | Settle-140, no button, no follow-up |
| 8 | **A split lands** | `SplitBranchesWindow` execution → toast with **Undo** (FeatureDesigns §4.8, "the arithmetic closes") | **Earns** — the toast *is* the way back (C-5) | Draw (execution bar) → Settle-140 (pill) |
| 9 | **Drag-to-rebase in flight** | `LabelDragGesture` ghost + drop target (Playbook §2.8) | **Earns clarity only** | Settle-120 to the 0.70 rest stop (ghost); Settle-130 (target) |
| 10 | **Loading resolves to content** | `AnalyticsView`, `RepoDashboardView`, search/palette groups | Functional, not celebration (`M-6`) | Exchange-130; skeletons never shimmer |
| 11 | **Overlay chrome arrives** | command palette, confirmation dialogs, scrims | Functional gravity (`M-2`) | Settle-140 of scrim + card as one layer |
| 12 | **Selection, badges, chip states** | selection rail, signature badge, assignment chips | Housekeeping | Settle-130 / Exchange-130 in reserved slots (E3) |

**Silent by classification** (ratifying Playbook §3 and `M-2`, restated as one rule): every *readout* is Still — the commit graph's paint and scroll, diff and intra-line emphasis rendering, the `MergeGutter`/`DiffMarginRenderer`/`BlameGutterMargin` gutters, theme switch (`M-4`, 0 ms by design — and A6's `ThemeChanged` handler is a *repaint*, never a transition), ahead/behind counts, the conservation ledger's arithmetic, and every mid-task counter tick (`3 of 6 conflicts resolved`, `12 commits remain` — the strip updates in place, FeatureDesigns §1.3.4). **Counters tick Still; only the terminal state change earns a primitive.** A readout that eased would be a readout you can't trust mid-ease.

### 4.2 The motion gates (named rules)

These extend G1–G5 (lanes), E1–E4 (encoding), and A1–A6 (accessibility) with seven *motion* gates. They are design-time gates; the shipping check is §4.6's harness note.

**D1 — The Earned-Moment Gate.** Timed motion attaches only to a moment on the §4.1 ledger. A new surface earns motion by adding its moment to the ledger with a verdict and a primitive — never by animating first and rationalizing after. (Bible `M-1`/`M-2` made law.)

**D2 — The One-Gesture Gate.** One moment = one primitive on one layer. Primitives never stack (no fade-plus-slide, no fade-plus-scale) and never chain beyond *in → hold → out*. A multi-beat sequence (clone, §4.4.3) is legal only when each beat is a distinct state transition with its own trigger — a sequence of states, not a choreography.

**D3 — The Alphabet Gate.** Every timed motion is one of §4.3's six primitives (plus Still) at its specified grade and curve. No new duration, no new curve, no `BackEase`/`BounceEase`/`ElasticEase`, nothing outside 120–150 ms (Draw ≤ 120; Still = 0). A motion that needs a new primitive amends this document first.

**D4 — The Still-Frame Gate.** The animated property is the `Opacity` of a **pre-colored** layer, a brush, or a control's intrinsic readout value — never a layout property; the space is pre-reserved (E3 extended to time). Pre-colored is load-bearing: a `BrushTransition` up from the `Transparent` keyword lerps through white and flashes (the Playbook's ghost-flash rule, ratified). Code-drawn visuals are Still by classification — they repaint per A6, they never tween.

**D5 — The Gravity Gate.** Motion celebrates completion; it never dramatizes danger. The more destructive or blocking a moment, the *less* motion it gets: a confirmation dialog arrives with only its carrier's standard Settle-140 (scrim + card as one layer); an inline safety block in an already-visible surface appears **Still, at full strength from its first frame**; nothing `DangerBrush`-toned ever pulses, shakes, blinks, or flashes. Severity rides the role (V-2) — if the celebration vocabulary is ever spent on danger, it stops meaning celebration.

**D6 — The Zero-Motion Gate.** Under a reduced-motion preference every primitive collapses to Still and *nothing else changes*: the pre-colored layer, the tokens, and the copy already carry the full meaning (Bible `M-7`; the temporal analogue of E1). A moment whose meaning fails without its primitive fails this gate — which is why every storyboard below lists its zero-motion form.

**D7 — The Theme-Blind Gate.** A primitive animates the opacity of token-colored layers, so it carries **no luminance direction**: the same Settle must read as *arrival* whether the layer is a luminous pill on Midnight charcoal or a deep-pine pill on Daylight paper. Motion may never assume "fading up from dark" (Bible `M-4` extended from the theme switch to all motion) — which is exactly why the alphabet contains no wipes, sweeps, or directional reveals.

### 4.3 The grammar — six primitives, three grades, five rest stops, two curves

**The curves** (the only two eased shapes, plus linear — no overshoot anywhere):

| Name | Avalonia class | cubic-bezier | Role |
|---|---|---|---|
| **taut-out** | `CubicEaseOut` | `cubic-bezier(0.215, 0.610, 0.355, 1.000)` | every entrance — a thread drawn taut, decelerating into rest without overshoot (`M-1`) |
| **taut-in** | `CubicEaseIn` | `cubic-bezier(0.550, 0.055, 0.675, 0.190)` | every exit — accelerating away |
| **linear** | `LinearEasing` | `cubic-bezier(0, 0, 1, 1)` | brush cross-fades and monotonic progress |

**The primitives** (the whole alphabet; Playbook rows mapped so existing citations stay valid):

| Primitive | Property | Duration · curve | Meaning | Playbook rows |
|---|---|---|---|---|
| **Settle** | `Opacity` 0 → rest stop, pre-colored layer | 120/130/140 ms · taut-out | something has *arrived* | G-2, G-3 (in), G-4, G-6, G-7 |
| **Release** | `Opacity` rest stop → 0 | 130 ms · taut-in | something has *finished and leaves* | G-3 (out), G-5 (out) |
| **Exchange** | two pre-composed layers in one reserved slot: Release-130 + Settle-130/140, simultaneous | 130–140 ms | one truth *replaces* another | G-5, the chip-text swap (FeatureDesigns §4.2) |
| **Recede** | `Opacity` 1 → 0.70 / 0.60 / 0.45 | 130 ms · taut-out | still present, *no longer the subject* (reverse = re-lighting, same grade) | the bisect dim (G-2 family) |
| **Draw** | a bound readout value (`ProgressBar.Value`) | ≤ 120 ms per reported step · linear, **monotonic** | honest progress (`M-6`) | G-8 |
| **Shift** | `Background`/`BorderBrush` between token values | **130 ms fixed, global** · linear | affordance state (hover, focus, enabled) | G-1 |
| *(Still)* | — | 0 ms | a readout, a theme switch, or a brake | G-9, G-10 |

**The three grades** — duration is picked by rule, not feel, inside the one budget:

- **140 ms — arrival with meaning.** Earned moments and overlay chrome: pills, the culprit card, the all-clear, scrim + dialog.
- **130 ms — state housekeeping.** Selection, badges, drop targets, chip/status exchanges, every Release, the fixed Shift.
- **120 ms — must not lag the hand.** Motion tied to live input: the drag ghost's fade-in, each Draw step.

**The five rest stops** — the only opacities a layer may settle at, a fixed scale like radius 6/8/12/999: **1.0** (the subject) · **0.70** (a copy in flight — the drag ghost, Playbook G-7) · **0.60** (stale-while-refreshing — the diff viewer's previous content during load, SurfaceDesigns §3.5) · **0.45** (eliminated but legible — bisect's narrowed threads, FeatureDesigns §1.8/§1.10, including Daylight's near-hairline reading, accepted there because eliminated rows are wayfinding, not data) · **0** (gone). No surface invents a sixth stop.

Because every primitive is an opacity or brush move over token-colored layers, the grammar is theme-blind by construction (D7): the five themes change what the layer *is*, never how it moves.

### 4.4 Storyboards — the five earned moments

Each storyboard: trigger → beat timeline (primitive · duration · curve) → what stays still → tokens → zero-motion form (D6) → the five-theme reading. Mechanisms are the Playbook's (a `DoubleTransition` on a pre-colored layer's `Opacity`; brushes via the global `BrushTransition`); final user-facing strings defer to Lane C's Microcopy inventory — the copy shown here passes the Bible's five-question gate and is the shape of record.

#### 4.4.1 A rebase lands — the thread rejoins

| Field | Spec |
|---|---|
| **Trigger** | A rebase completes (the drag-to-rebase flyout's `Rebase feature onto main`, C-2, or the branch menu) — `GitServices` reports success to `ShowNotification` |
| **Beat 1 — Still** | The weave repaints **composed** with the new topology (`CommitGraphCanvas`, `M-2`). Explicitly banned: any FLIP-style choreography of rows sliding to their new lanes — the graph is a readout, and an instrument that animates its own reading is lying for the duration |
| **Beat 2 — Settle-140** | The pill fades in over the settled graph: `Rebased feature onto main.` (T-1) — `Opacity` 0 → 1, taut-out, `cubic-bezier(0.215, 0.610, 0.355, 1.000)` |
| **Beat 3 — hold** | 3 000 ms (`RepoDashboardViewModel.cs:219`'s shipped timer — a hold is stillness, outside the motion budget) |
| **Beat 4 — Release-130** | `Opacity` 1 → 0, taut-in, `cubic-bezier(0.550, 0.055, 0.675, 0.190)` |
| **Stays still** | Pill position (bottom-right, `Margin=20`), size, radius-999, shadow; the graph, the selection, the branch pill — nothing else acknowledges the event |
| **Tokens** | `SuccessBrush` fill, `OnAccent` text (`Border.toast`); graph lanes `Lane1`–`Lane5` |
| **Zero-motion** | Pill appears and disappears instantly on the same timer; the graph already repaints instantly |
| **Five themes** | The pill is each theme's Success on its own field — Midnight `#42B968` on charcoal (arrival of light), Daylight `#0E6B37` on paper (arrival of weight — §3.3's pine, `OnAccent` white at 6.61), Command Deck mint `#34D399`, Atelier sage `#8FBC7F` with near-black ink `#171512`, Aurora spring `#4ADE80`. Opposite luminance directions, identical gesture — the D7 proof case |

#### 4.4.2 A conflict fully resolved — the weave closes

| Field | Spec |
|---|---|
| **Trigger** | The last unresolved chunk's `IsResolved` flips true → `IsFullyResolved` (`ConflictResolverWindowViewModel.cs:64`) |
| **Beats 0…n — Still** | Every individual accept/reject is a readout repaint: the `MergeGutter` bands, the `GlyphPlate` pills (§3.7), and the `StatusText` counter (`3 of 6 conflicts resolved`) all swap instantly. Counters tick Still (§4.1) — mid-task, the instrument must always show the exact current truth |
| **Terminal beat — Exchange-140** | In the toolbar's fixed `StatusText` slot (`ConflictResolverWindow.axaml:18`), the counter cross-fades to the closing fact: `All 6 conflicts resolved.` — outgoing layer Release-130 taut-in, incoming layer Settle-140 taut-out, same reserved slot (E3), and the incoming line steps `TextMuted` → `TextPrimary`: the fact has earned weight |
| **Companion — Shift** | `IsFullyResolved` enables `MarkResolvedCommand`: the **Mark Resolved** `Button.Accent` crosses from its disabled to enabled fill via the global 130 ms Shift — the way forward lights up, the accent-hands-forward invariant (FeatureDesigns §1.9/SurfaceDesigns §4.6). This is the entire celebration inside the window: no toast on top of an open resolver (one moment, one meaning) |
| **Hand-off** | Marking resolved closes the window (Still); when the *merge itself* later commits, the Bible's `M-1` pill (`Merged feature into main.`, Playbook §2.10) is the terminal celebration of the whole chain — this storyboard deliberately does not duplicate it |
| **Stays still** | The three editors, both gutters, every band fill, the footer buttons' geometry — the safety surface never moves (D5 adjacency: this window exists because of danger) |
| **Tokens** | `TextMuted`/`TextPrimary` (status line), `AccentBrush`/`OnAccent` (Mark Resolved), `DiffAddedBg`/`DiffRemovedBg` + `GlyphPlate` (bands, Still) |
| **Zero-motion** | Status text and button state swap instantly — the words and the lit accent are the full meaning |
| **Five themes** | The closing line is a text-role change, identical in all five (A1 gates both roles everywhere). The lit accent is each theme's signature: Midnight violet `#8B8BF5`, Daylight indigo `#4B4ED8` (§3.3), Command Deck teal `#2DD4BF`, Atelier copper `#D8A25A`, Aurora `#4FD1C5`. On Daylight the enabled accent *darkens* the button — D7: enabling is a brush truth, not a brightness direction |

#### 4.4.3 A clone completes — the first weave

| Field | Spec |
|---|---|
| **Trigger** | `OnTransferProgress`/`OnCheckoutProgress` reach 100 % (`CloneDashboardView`, monotonic by contract) |
| **Beat 1 — Draw** | The bar glides between reported percents, ≤ 120 ms linear per step, strictly monotonic (`M-6`; Playbook §2.6 ratified). Unknown phases are indeterminate, never a fake number |
| **Beat 2 — hold 150 ms** | The bar rests at its 100 % frame — one readable beat at the top of the budget, so the eye confirms *done* before anything leaves. A hold, not a motion |
| **Beat 3 — Release-130** | The overlay (scrim `#C0000000`, the allowed literal, + card) fades out as one layer, taut-in |
| **Beat 4 — Still** | The workspace paints **composed**: the full weave at 60 fps, no spinner-to-content jank — SurfaceDesigns §5.3's "hook" beat (t = 20–35 s of the 60-second script), the first-run moment the OOBE path is built around. The absence of motion *is* this beat's delight |
| **Beat 5 — Settle-140** | The pill: `Cloned react into ~/code/react.` (T-1, path in mono) → hold 3 000 ms → Release-130. If the tree is clean (it is, post-clone), the staging panel's quiet `ES-4` affirmation settles once alongside — two layers, one gesture each, no stagger |
| **Stays still** | Layout of the arriving workspace; the graph draws, it does not fade in; no panel slides into place |
| **Tokens** | `AccentBrush` (bar fill), `SurfaceCard` (track), `TextMuted` (% label), `SuccessBrush`/`OnAccent` (pill), `Lane1`–`Lane5` (the weave) |
| **Zero-motion** | Bar jumps to each reported value (still monotonic — honesty survives, D6), overlay hides instantly, pill appears/disappears instantly |
| **Five themes** | The composed first paint is each theme's thesis statement: Midnight's jewel threads, Daylight's ink-on-paper, Command Deck's tactical traces, Atelier's bench, Aurora's luminous night (Part 1 §1.4's five characters). The sequence's only luminance event is the scrim leaving — identical in all five because the scrim is the one sanctioned literal |

#### 4.4.4 A push succeeds — the quiet dispatch

| Field | Spec |
|---|---|
| **Trigger** | `PushCommand` completes (`RepoDashboardViewModel`; also the branch-browser push) |
| **Beat 1 — Still** | Ahead/behind counts on the navbar branch pill and the "last fetched" label update instantly — readouts |
| **Beat 2 — Settle-140 / hold 3 000 ms / Release-130** | The pill: `Pushed feature to origin.` — the Bible V-7 exemplar, verbatim. This is the grammar's reference implementation: the smallest earned moment, one layer, one gesture |
| **The failure fork** | A non-fast-forward rejection is **not** this moment's error variant — it is a decision point, so it gets a pattern-E dialog under D5 gravity (T-3: an error needing a decision is never a toast). `Border.toast.error` (`DangerBrush` fill) remains only for informational failures that need no decision, and it uses the identical Settle/hold/Release — same gesture, different fact; the brush carries the difference (never motion: no shake, D5) |
| **Stays still** | Everything else. A push that worked is routine; the pill's modesty is the product's confidence |
| **Tokens** | `SuccessBrush`/`OnAccent` (pill); `DangerBrush` (error variant); `Button.Pill` (branch chip, Still) |
| **Zero-motion** | Instant pill on the same 3 s timer |
| **Five themes** | As §4.4.1's pill row. One added watch-item: on Command Deck the Success mint `#34D399` neighbors the teal accent `#2DD4BF` (the known G5 kinship) — the pill's radius-999 silhouette and bottom-right anchor, not its hue, are what say "notification," which is E1 doing its job in the motion layer |

#### 4.4.5 A hazard blocked — the instant brake

| Field | Spec |
|---|---|
| **Trigger** | A safety mechanism refuses: the pre-commit scanner finds a blocker (`AwaitingOverride`, `PreCommitFindingsView`), force-with-lease refuses a push, `UndoBlockedException` blocks an undo, bisect refuses a dirty tree (FeatureDesigns §1.3.3), a stale `index.lock` is found |
| **The motion** | **Still — 0 ms, by design (D5).** An inline block in an already-visible surface renders at full strength from its first frame: the banner, its `DangerBrush` hairline border, Part 2's `SeverityBlockerIcon` octagon, and the V-2 copy arrive complete. 140 ms of a blocker becoming visible is 140 ms of a brake not yet applied — and spending the Settle vocabulary on danger would teach the eye that arrival means celebration even when it means *stop* |
| **Carrier exception** | When the block lives in overlay chrome (the force-with-lease confirmation, a pattern-C dialog), the **carrier** gets its standard §4.1-row-11 Settle-140 of scrim + card as one layer — gravity, not play (`M-2`) — and the hazard content inside it gets no second motion, ever |
| **Banned outright** | Pulsing the danger button, shaking the dialog, blinking the octagon, flashing the banner, animating attention toward the block. The block holds attention by being the only `DangerBrush`-bordered object on a calm surface (V-2: severity rides the role) — the stillness *is* the authority, the loom stopping its shuttle without theater |
| **Stays still** | Also the layout: the blocker banner's slot is reserved where it can toggle within a visible panel (E3), so the brake never shoves the page. Dismissal, when it happens, is a Release-130 — leaving calmly is not celebration |
| **Tokens** | `DangerBrush` (border, octagon, headline), `SurfaceCard` (banner field, per A2's re-surfacing — never `DiffRemovedBg`), `TextPrimary` (body) |
| **Zero-motion** | Identical — this moment is already the zero-motion form, which is the point: the one moment on the ledger that is *born* D6-compliant |
| **Five themes** | The octagon + border on `SurfaceCard`: Midnight `#F87171`, Daylight `#B33030` (§3.3 — on paper the danger ink is *darker* than the field, so a flash would be doubly wrong; stillness reads as authority in both polarities, D7), Command Deck `#FB7185`, Atelier oxblood `#E5726A`, Aurora rose `#F8719D`. A4 holds every pairing ≥ 3:1 as a mark and 4.5:1 as text |

### 4.5 Lane B's named moments, ratified

The four delight moments SurfaceDesigns/FeatureDesigns coined are hereby vocabulary of record, each expressed in the grammar (no corrections needed — all four were written inside the family):

| Named moment | Source | In the grammar |
|---|---|---|
| **The narrowing weave** | FeatureDesigns §1.8 (bisect) | Recede-130 to the 0.45 rest stop on eliminated threads; the count ticks Still; the culprit card is one Settle-140 — no confetti at the landing |
| **The lit thread** | FeatureDesigns §2.6 (global search) | **Still.** Match spans are `AccentBrush` + weight — rendering, not motion. Ratified as the exemplar of delight with zero animation: the metaphor lives in the paint (`M-1`'s note on the graph's round caps, extended to text) |
| **The watch at rest** | FeatureDesigns §3.7 (multi-repo dashboard) | Settle-140 of the all-clear line (ES-4's license); everything else on the surface earns its silence by being absent |
| **The arithmetic closes** | FeatureDesigns §4.8 (split wizard) | Ledger ticks Still; assignment chips Exchange-130 in fixed-width slots; the landing toast Settle-140 with **Undo** riding it (C-5) |

The pattern the four share, stated once as the vocabulary's thesis: **Mainguard's delight is comprehension arriving, and comprehension arrives at most 140 ms at a time.**

### 4.6 Deltas and notes for implementers

1. **The toast exit is currently a hard cut.** `RepoDashboardView.axaml:63` binds the pill to `IsVisible`, so the Release never plays and even the Settle is at the mercy of the visibility toggle. Adopt the Playbook §2.5 mechanism: drive `Opacity` through a bool→double conversion, keep the pill mounted during the fade, drop `IsHitTestVisible` at 0. This one change implements storyboards 4.4.1, 4.4.4, and beat 5 of 4.4.3.
2. **The terminal status Exchange (4.4.2)** wants two pre-composed `TextBlock`s in the existing toolbar cell (`ConflictResolverWindow.axaml:18`), opacity-crossed — not a re-templated control. `StatusText` already computes both truths (`ConflictResolverWindowViewModel.cs:60–62`); the view splits them.
3. **No new tokens, no new icons.** Part 4 spends only what Parts 1–3 minted: the pill uses `Border.toast`, the brake uses §2.4's octagon and §3.3's Daylight inks, the bands use §3.5's tints and §3.7's `GlyphPlate`. Motion is the one layer of the system that is deliberately theme-file-empty.
4. **Transitions live in `App.axaml` styles, once** — like the G-1 `BrushTransition`. A per-view duplicate of a primitive is drift; a per-view *new* duration is a D3 violation.
5. **Reduced motion**: the D6 collapse is a single app-level switch (the Playbook's `M-7` row assumes one; honor the OS signal when Avalonia exposes it on the platform). Implementation shape: the switch zeroes the shared transition durations — no per-surface logic, because no surface's meaning depends on its primitive (that is what D6 gated at design time).
6. **Shipping gate**: extend the ThemeRefinement §7 / PolishSpec §7 harness with one motion assertion per earned moment — capture at t=0 and t=200 ms and assert (a) the end frame is pixel-stable (nothing oscillates — no-bounce, verified), and (b) with reduced motion on, t=0 already equals the end frame (D6, verified). The five-theme render harness already covers D7, since the primitives are theme-invariant by construction.

### 4.7 Verification record (the Part 4 self-gate)

- **Coverage** — the five required moments each have a full storyboard (§4.4.1–§4.4.5: trigger, beats, curves as named class + cubic-bezier, properties, tokens, zero-motion form, five-theme reading, no blank cells); Lane B's four named moments are ratified into the grammar (§4.5); the Playbook's twelve briefs map onto the six primitives with zero orphans (§4.3 table, rows G-1–G-10).
- **D1** — every timed motion in this part appears on the §4.1 ledger; the silent list covers every readout named in Parts 1–3 (graph, gutters, ramps, counters).
- **D2** — no storyboard stacks primitives on one layer; the two sequences (clone, rebase) are beat-per-state-transition, each beat a single primitive.
- **D3** — durations used: 0 / ≤120 / 130 / 140 / the 150 ms hold (a hold, not a motion) — all inside 120–150; curves used: taut-out, taut-in, linear only; overshoot easings appear only in the ban list.
- **D4** — animated properties across all storyboards: `Opacity` of pre-colored layers, the global brush Shift, `ProgressBar.Value`. Zero layout properties; every toggling element sits in a reserved slot (E3 citations in 4.4.2, 4.4.5).
- **D5** — the hazard storyboard is Still; the confirmation carrier is the standard overlay Settle with no inner motion; the ban list names pulse/shake/blink/flash explicitly.
- **D6** — every storyboard states its zero-motion form; 4.4.5 is identical with and without motion by construction.
- **D7** — all primitives are opacity/brush over tokens; both luminance-polarity cases are argued at the pill (4.4.1) and the brake (4.4.5); no wipe, sweep, or directional reveal exists in the alphabet.
- **Bible conformance** — `M-1` (earned set matches and extends its list), `M-2`/`M-4` (silent list ratified, theme switch 0 ms), `M-3` (budget restated and obeyed), `M-5` (no bounce anywhere), `M-6` (Draw is monotonic-or-indeterminate), `M-7` (D6), V-2/V-7 (all storyboard copy passes the five-question gate; final strings defer to Lane C).

---

**Document status: complete.** Parts 1–4 together are the design-system specification of record: the decoupled lane palette (G1–G5), the state-encoding icon system (E1–E4), the five-theme accessibility contract (A1–A6), and the motion grammar (D1–D7) — every value verified, every rule named, every surface real.
