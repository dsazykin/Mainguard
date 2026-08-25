# Theme Refinement — Color Retune for Lane & Diff Separability

> **Authority note (2026-07-12 consolidation).** The proposed lane values in **§4.1 are superseded** by
> [`docs/design/DesignSystem.md`](../design/DesignSystem.md) **Part 1** (which confirms this document's
> diagnosis, re-measures the §4.1 values against stricter gates, and ratifies a corrected palette).
> Still authoritative here: **§4.2** (diff add/remove backgrounds — the proposal of record, gated by
> DesignSystem Part 3), **§6** (the proposed Loom Meridian sixth theme), and **§7** (the shipping
> verification gates, which DesignSystem Part 1 adopts with updated thresholds). Nothing else changed.

**Status: DESIGN SPEC — proposed values only. No live theme file is edited by this document.**
Retuning a live token is a build-affecting change: it requires human sign-off, full-token-coverage
verification, and a pass through the headless render harnesses (see §7). Every hex below lives *in this
doc as a proposal*; `Mainguard.App.Shell/Themes/*.axaml` is untouched.

Conforms to the one design system: values are named by **role**, not hue (DESIGN.md §2, "The
Semantic-Not-Literal Rule"); the **One-Accent Rule** is preserved (no theme's `AccentBrush` /
`AccentHover` / `AccentSelection` is changed); shape, spacing, typography, and motion are out of scope
and unchanged. New theme name cited against the Voice & Delight Bible naming rules (**N-1**, **N-5**).

---

## 1. Scope & method

**What this refines.** PRODUCT.md §Accessibility flags that `Lane1`–`Lane5` and the diff add/remove
colors "currently rely on hue distinction" — a problem for color-vision deficiency (CVD) and for
lane/lightness overlap. This spec audits and retunes exactly those tokens across all five shipping
themes, and proposes an optional sixth theme built CVD-first.

**What it deliberately leaves alone.** Surfaces, text, accent, and the semantic
`Success/Danger/Warning/Info` fills already clear WCAG AA for their use (text ≥ 4.5:1, large/graphical
≥ 3:1) and carry meaning through *component role* plus copy, not through the graph's raw hue channel.
They are not retuned here; only the two hue-dependent categories the product flags — **graph lanes** and
**diff line backgrounds** — change.

**How the numbers were computed.** Contrast is WCAG 2.1 relative-luminance ratio. "Hue" and "L"
(lightness) are HSL. **deutL** is the lightness of each color after a Brettel/Viénot-style
**deuteranopia** projection (the most common severe red-green CVD) — it approximates *what luminance a
red-green-blind viewer perceives*. These are engineering approximations to prioritize the retune, **not
a substitute** for the render-harness + contrast-gate verification in §7. All figures are reproducible;
the analysis scripts are archived with this change.

**The full token contract (every theme must define all 32).** A token missing from one theme is a
runtime bug the compiler cannot catch (AGENTS.md §Theming). Any proposed theme (§6) lists all of them.

| Group | Tokens |
|---|---|
| Surfaces (8) | `SurfaceWindow` · `SurfacePanel` · `SurfaceDeep` · `SurfaceCard` · `SurfaceHover` · `SurfaceHoverGhost` · `ButtonBg` · `BorderHairline` |
| Text (3) | `TextPrimary` · `TextMuted` · `OnAccent` |
| Accent (3) | `AccentBrush` · `AccentHover` · `AccentSelection` |
| Semantic (6) | `SuccessBrush` · `SuccessHover` · `DangerBrush` · `DangerHover` · `WarningBrush` · `InfoBrush` |
| Lanes (5) | `Lane1` · `Lane2` · `Lane3` · `Lane4` · `Lane5` |
| Diff (5) | `DiffAddedBg` · `DiffRemovedBg` · `DiffAddedEmphasis` · `DiffRemovedEmphasis` · `DiffWhitespaceMarker` |
| Blame (2) | `BlameAgeNew` · `BlameAgeOld` |

---

## 2. Diagnosis — why hue alone fails

**Finding A — under deuteranopia the five lanes collapse into two hue clusters.** Projecting every
theme's lanes through a deuteranopia simulation, the perceived hue of violet, teal, rose, sky, and
indigo all land at ≈ 239° (one indistinguishable "blue"), while amber/gold lands at ≈ 60° ("yellow").
So a red-green-blind viewer sees **at most two hue families** on the graph. Whatever separates the four
"blue-cluster" lanes from each other must therefore be **lightness**, not hue. In the shipping themes the
lanes' perceived lightnesses are bunched (e.g. Midnight's four blue-cluster lanes sit at deutL
0.65–0.78; Atelier's five lanes sit within a 0.06-wide band), so several lane pairs are near-invisible
to CVD viewers.

**Finding B — several lanes are hex-identical to a semantic token.** In Midnight and Daylight,
`Lane4` == `WarningBrush` and `Lane5` == `InfoBrush` *to the hex*; in Command Deck `Lane3` ==
`SuccessBrush` and `Lane1` ≈ `SuccessBrush` (Δhue 14°). DESIGN.md §2 requires lanes be "deliberately
decoupled from these semantic colors so graph topology never accidentally implies status." Identical
hexes violate that decoupling. `Lane1` is also hex-identical to `AccentBrush` in every theme; the accent
is not a *semantic* status, so this is lower-severity, but the retune nudges `Lane1` into the staircase
so it is no longer a literal duplicate while staying in the accent hue family (identity preserved).

**Finding C — diff add vs remove are near-identical in luminance and collapse under CVD.** Across all
five themes the added-line and removed-line backgrounds differ in luminance by only ~2–6%
(add-vs-remove contrast 1.02–1.06) and, being a green vs a red at matched lightness, they project to the
*same* deuteranopia hue (Δ ≈ 0.7°). A red-green-blind viewer cannot tell an added line from a removed
one by color at all. The gutter +/- glyph is the only surviving signal — which means color is doing no
work for these users today.

**The retune strategy (applied uniformly).**
1. **Lane lightness staircase.** Give each theme's five lanes a deliberate, monotonic *lightness*
   staircase so they remain orderable by luminance even when hue is unavailable — target ≥ 0.07
   separation in deutL between every pair. Vary chroma too, not only hue.
2. **Contrast-bounded band.** The staircase lives inside a legibility band: on a dark theme the darkest
   lane must still clear ~3:1 against `SurfacePanel` (graph strokes are graphical objects, WCAG 1.4.11);
   on the light theme the lightest lane must still clear it. This caps how far the staircase can spread.
3. **Decouple lanes from semantics.** Move `Lane4` off the `Warning` hex and `Lane5` off the `Info`
   hex (and Command Deck's greens apart), so no lane equals a status color.
4. **Luminance-separated, hue-shifted diffs.** Push added lighter/greener and removed darker while
   shifting removed toward **magenta-rose** (adds a blue component) so it no longer collapses onto green
   under deuteranopia; target add-vs-remove ≥ ~1.15 grayscale separation. Keep both as subtle tints so
   diff text stays readable over them.

`DiffAddedEmphasis`/`DiffRemovedEmphasis` (0x66-alpha word-level tints of Success/Danger over an
already-tinted line) and `DiffWhitespaceMarker` remain legible over the retuned line backgrounds and are
left as-is here; `BlameAgeNew`→`BlameAgeOld` is a *sequential* lightness ramp (interpolated by commit
age), inherently CVD-safe, and unchanged. All three still belong to the full contract every theme must
define.

---

## 3. Per-theme audit (current values)

Contrast = vs that theme's `SurfacePanel` (the graph's surface) for lanes, vs `SurfaceDeep` (the
editor) for diffs. "deutL" = perceived lightness under deuteranopia. Flags: **HUE↔SEM** hex/near-hue
identity with a semantic token; **CVD-CLASH** lightness too close to a same-cluster lane to survive CVD;
**LOW-Δ** diff add/remove luminance nearly equal.

### 3.1 Midnight Loom (default, dark) — `SurfacePanel #14171C`, `SurfaceDeep #0B0D10`
| Token | Current | Contrast | Hue / deutL | Issue |
|---|---|---|---|---|
| `Lane1` | `#8B8BF5` | 6.08 | 240° / 0.75 | hex-identical to `AccentBrush` |
| `Lane2` | `#F472B6` | 6.78 | 329° / 0.65 | CVD-CLASH with Lane1 (deutL 0.75 vs 0.65 tight; blue-cluster) |
| `Lane3` | `#2DD4BF` | 9.65 | 172° / 0.70 | blue-cluster; deutL 0.70 near Lane5 0.78 |
| `Lane4` | `#E3B341` | 9.23 | 42° / 0.50 | **HUE↔SEM**: hex-identical to `WarningBrush` |
| `Lane5` | `#58A6FF` | 7.11 | 212° / 0.78 | **HUE↔SEM**: hex-identical to `InfoBrush`; CVD-CLASH w/ Lane1 |
| `DiffAddedBg` | `#11271B` | 1.23 | green | **LOW-Δ**: add-vs-remove contrast **1.02**, deut-collapse |
| `DiffRemovedBg` | `#33191E` | 1.20 | red | **LOW-Δ** (see above) |

### 3.2 Daylight Loom (light) — `SurfacePanel #F7F8FB`, `SurfaceDeep #FFFFFF`
| Token | Current | Contrast | Hue / deutL | Issue |
|---|---|---|---|---|
| `Lane1` | `#6467E8` | 4.26 | 239° / 0.65 | hex-identical to `AccentBrush` |
| `Lane2` | `#C13584` | 4.81 | 326° / 0.44 | blue-cluster under CVD |
| `Lane3` | `#0D9488` | **3.53** | 175° / 0.48 | marginal contrast (>3:1 graphical, but low); CVD-CLASH w/ Lane5 |
| `Lane4` | `#A9770F` | **3.71** | 40° / 0.29 | **HUE↔SEM**: hex-identical to `WarningBrush`; low contrast |
| `Lane5` | `#0B78CE` | 4.31 | 206° / 0.58 | **HUE↔SEM**: hex-identical to `InfoBrush` |
| `DiffAddedBg` | `#DDF3E4` | 1.17 | green | **LOW-Δ**: add-vs-remove **1.05**, deut-collapse |
| `DiffRemovedBg` | `#FBE2E2` | 1.23 | red | **LOW-Δ** (see above) |

### 3.3 Command Deck (dark) — `SurfacePanel #0E1114`, `SurfaceDeep #07090B`
| Token | Current | Contrast | Hue / deutL | Issue |
|---|---|---|---|---|
| `Lane1` | `#2DD4BF` | 10.17 | 172° / 0.70 | ≈ `SuccessBrush` (Δhue 14°) and == `AccentBrush` |
| `Lane2` | `#C084FC` | 7.17 | 270° / 0.78 | blue-cluster |
| `Lane3` | `#34D399` | 9.85 | 158° / 0.63 | **HUE↔SEM**: hex-identical to `SuccessBrush`; CVD-CLASH w/ Lane1 (deut Δ 3°, Lδ 0.05) |
| `Lane4` | `#FBBF24` | 11.34 | 43° / 0.48 | **HUE↔SEM**: hex-identical to `WarningBrush` |
| `Lane5` | `#38BDF8` | 8.84 | 198° / 0.79 | **HUE↔SEM**: hex-identical to `InfoBrush`; CVD-CLASH w/ Lane1 |
| `DiffAddedBg` | `#0D251F` | 1.24 | green | **LOW-Δ**: add-vs-remove **1.06** |
| `DiffRemovedBg` | `#2A161B` | 1.17 | red | **LOW-Δ** |

### 3.4 Atelier (warm dark) — `SurfacePanel #1D1A16`, `SurfaceDeep #121009`
| Token | Current | Contrast | Hue / deutL | Issue |
|---|---|---|---|---|
| `Lane1` | `#D8A25A` | 7.62 | 34° / 0.59 | ≈ `WarningBrush` (Δhue 8°) and == `AccentBrush` |
| `Lane2` | `#C08BB8` | 6.29 | 309° / 0.66 | **whole palette in a 0.06 lightness band** — every lane pair flags LIGHT-CLASH |
| `Lane3` | `#8FBC7F` | 7.98 | 104° / 0.59 | **HUE↔SEM**: hex-identical to `SuccessBrush`; CVD-CLASH |
| `Lane4` | `#7FA6C4` | 6.73 | 206° / 0.69 | ≈ `InfoBrush` (Δhue 4°) |
| `Lane5` | `#C4766B` | 5.08 | 7° / 0.48 | ≈ `DangerBrush` (Δhue 4°) |
| `DiffAddedBg` | `#1E2A1A` | 1.27 | green | **LOW-Δ**: add-vs-remove **1.03** |
| `DiffRemovedBg` | `#33201D` | 1.24 | red | **LOW-Δ** |

*Atelier is the worst case: its muted craftsman palette packs all five lanes into deutL 0.59–0.66 and
puts four of five lanes within 8° of a semantic token. It needs the most aggressive lightness spread
while keeping its warm, low-chroma identity.*

### 3.5 Loom Aurora (cool dark) — `SurfacePanel #161930`, `SurfaceDeep #0C0E1A`
| Token | Current | Contrast | Hue / deutL | Issue |
|---|---|---|---|---|
| `Lane1` | `#4FD1C5` | 9.26 | 175° / 0.73 | == `AccentBrush` |
| `Lane2` | `#A78BFA` | 6.35 | 255° / 0.78 | blue-cluster; CVD-CLASH w/ Lane3 |
| `Lane3` | `#F472B6` | 6.52 | 329° / 0.65 | ≈ `DangerBrush` (Δhue 12°); CVD-CLASH w/ Lane5 (Lδ 0.02) |
| `Lane4` | `#FACC6B` | 11.44 | 41° / 0.63 | **HUE↔SEM**: hex-identical to `WarningBrush` |
| `Lane5` | `#60A5FA` | 6.79 | 213° / 0.78 | **HUE↔SEM**: hex-identical to `InfoBrush` |
| `DiffAddedBg` | `#10291F` | 1.24 | green | **LOW-Δ**: add-vs-remove **1.02** |
| `DiffRemovedBg` | `#351A26` | 1.21 | red | **LOW-Δ** |

---

## 4. Proposed retuned values

Only the flagged tokens change. `AccentBrush`/`AccentHover`/`AccentSelection`, all surfaces, all text,
and all six semantic tokens are **unchanged** — the One-Accent Rule and each theme's identity hold.
Each lane table lists the new hex, its computed contrast vs `SurfacePanel`, and its deutL; every theme's
five lanes clear a **min pairwise deutL gap ≥ 0.078** (CVD-orderable) and a **min contrast ≥ ~3.0**
(graphical-object floor). Two values sit right at the 3:1 floor and are flagged to confirm/raise in the
harness.

### 4.1 Retuned lanes

| Theme | `Lane1` | `Lane2` | `Lane3` | `Lane4` | `Lane5` | min deutL gap · min contrast |
|---|---|---|---|---|---|---|
| **Midnight** | `#9A9AF4` (7.10 / .78) | `#E860A4` (5.68 / .58) | `#C0EAE3` (13.8 / .88) | `#E8A217` (8.22 / .40) | `#0B87F5` (4.96 / .68) | 0.098 · 4.96 |
| **Daylight** | `#3232E2` (7.35 / .54) | `#C62A83` (4.87 / .42) | `#075B55` (7.50 / .30) | `#7B420A` (7.53 / .18) | `#138BE6` (3.37 / .66) | 0.118 · 3.37 |
| **Command Deck** | `#7AE7D9` (12.9 / .82) | `#9D4AF0` (4.21 / .66) | `#34CF82` (9.37 / .58) | `#FCE091` (14.6 / .74) | `#086EA1` (3.39 / .48) | 0.079 · 3.39 |
| **Atelier** | `#E4C399` (10.4 / .70) | `#AC57A5` (3.85 / .54) | `#C7E0BF` (12.3 / .80) | `#3A698E` (**2.96** / .46) | `#DA9A91` (7.44 / .62) | 0.080 · 2.96 ⚠ |
| **Loom Aurora** | `#79E0D5` (11.1 / .80) | `#8B69F3` (4.45 / .70) | `#F365B1` (6.01 / .62) | `#FDF0D7` (15.3 / .90) | `#0B63C9` (**3.00** / .54) | 0.078 · 3.00 ⚠ |

⚠ Atelier `Lane4` (2.96) and Aurora `Lane5` (3.00) are at the graphical-object floor under the
approximate model. Confirm in the harness; if either reads short, lighten it ~4–6% (which compresses its
deutL gap to the neighbor by the same amount — still > 0.06, an acceptable CVD margin).

**Decoupling achieved.** After the retune no lane is hex-identical to any semantic token: Midnight
`Lane4 #E8A217` ≠ `Warning #E3B341` and `Lane5 #0B87F5` ≠ `Info #58A6FF`; Command Deck `Lane3 #34CF82` ≠
`Success #34D399`; Aurora likewise. `Lane1` is nudged into the staircase and no longer duplicates
`AccentBrush` while staying in the accent hue family.

**Identity preserved.** Each theme keeps its hue signature — Midnight violet/rose/teal/amber/sky,
Atelier's warm low-chroma copper/mauve/sage/slate/terracotta, Aurora's luminous teal/violet/rose/gold —
only their lightness relationships are re-spaced.

### 4.2 Retuned diff backgrounds

Added → lighter/greener; removed → darker and shifted toward magenta-rose so it no longer collapses onto
green under deuteranopia. Both remain subtle tints (contrast vs editor ~1.2–1.5, text stays readable).

| Theme | `DiffAddedBg` (new) | `DiffRemovedBg` (new) | add-vs-remove | was |
|---|---|---|---|---|
| **Midnight** | `#123726` | `#3A1622` | **1.21** | 1.02 |
| **Daylight** | `#D9F2E1` | `#F5C6CF` | **1.28** | 1.05 |
| **Command Deck** | `#0C3226` | `#2E1220` | **1.23** | 1.06 |
| **Atelier** | `#1D3420` | `#3C1C1C` | **1.14** | 1.03 |
| **Loom Aurora** | `#0E3325` | `#3A162B` | **1.14** | 1.02 |

Grayscale separation roughly doubles, and removed no longer projects onto added under deuteranopia. On
the **light** theme, luminance headroom is small even after tuning (1.28), so the gutter +/- glyph
remains the guaranteed non-color signal there (already present in `DiffViewerView`) — a reminder that
**color is never the only signal of add/remove** (mirrors Bible M-7's "meaning survives with zero
[color] cue").

---

## 5. Rules & citations honored

- **DESIGN.md §2 Semantic-Not-Literal** — lanes retuned *away* from semantic hexes; the decoupling the
  rule mandates is now real, not just asserted.
- **DESIGN.md One-Accent Rule** — no `Accent*` token changed; each view still has exactly one accent.
- **DESIGN.md §6 "verify in Midnight (dark) and Daylight (light)"** — the verification plan (§7) renders
  both, plus the other four themes.
- **PRODUCT.md Design Principle 1** — one design system; only color tokens move; shape/space/type fixed.
- **PRODUCT.md §Accessibility** — this spec is the "revisit" that section invites once CVD-safe
  lane/diff requirements surface.
- **Bible N-5** — every new color is a role-named token defined in *every* `Themes/*.axaml`.

---

## 6. Optional sixth theme — **Loom Meridian** (proposed, not wired)

**Name rationale (Bible N-1).** A theme is named for *a mood of light or a place of craft*, Title Case,
ideally carrying "Loom", **never** its literal palette. **Meridian** = the sun at its highest — the
clearest, most even light of the day — and a meridian is a precise navigational line, echoing the
Precision Loom North Star. It names a *quality of light*, not a color, so it survives any future retune.
Carries "Loom". Compliant with N-1; new tokens are role-named per **N-5**.

**Concept.** The **CVD-first** sibling: a cool, high-contrast dark theme whose lanes and diffs are built
on the §2 staircase from the start, whose `TextPrimary` clears 15:1 on every surface, and whose accent
is a clear cyan-azure ("meridian daylight") kept distinct from `Info` (pushed to indigo) so the One
Accent never reads as a status. It is a **complete** theme — all 32 tokens below.

### 6.1 Full token table

| Token | Hex | Note |
|---|---|---|
| `SurfaceWindow` | `#0E1217` | cool near-black |
| `SurfacePanel` | `#131A22` | floating card surface |
| `SurfaceDeep` | `#090C10` | editor/diff |
| `SurfaceCard` | `#1A2330` | inputs, raised cards |
| `SurfaceHover` | `#27313F` | hover / neutral selection |
| `SurfaceHoverGhost` | `#0027313F` | `SurfaceHover` at 0 alpha |
| `ButtonBg` | `#1F2A36` | neutral button fill |
| `BorderHairline` | `#2C3745` | 1px borders |
| `TextPrimary` | `#EEF2F8` | 15.6:1 on panel |
| `TextMuted` | `#98A4B4` | 6.9:1 on panel |
| `OnAccent` | `#090C10` | text on accent/success/danger |
| `AccentBrush` | `#33B1E0` | cyan-azure; 7.1:1 on panel |
| `AccentHover` | `#5CC4EC` | |
| `AccentSelection` | `#2633B1E0` | translucent accent tint |
| `SuccessBrush` | `#35C48C` | teal-green (8.8:1 w/ OnAccent) |
| `SuccessHover` | `#57D3A3` | |
| `DangerBrush` | `#FF6E8A` | rose-red (7.3:1 w/ OnAccent), CVD-distinct from Success by lightness |
| `DangerHover` | `#FF8CA1` | |
| `WarningBrush` | `#E7B23A` | amber |
| `InfoBrush` | `#7C8CF5` | indigo — decoupled from the cyan accent |
| `Lane1` | `#77C7E7` | azure · deutL 0.80 · 9.3:1 |
| `Lane2` | `#9461ED` | violet · deutL 0.68 · 4.3:1 |
| `Lane3` | `#EB5AA7` | rose · deutL 0.58 · 5.5:1 |
| `Lane4` | `#EDB020` | amber · deutL 0.44 · 9.0:1 (≠ Warning) |
| `Lane5` | `#DDF5DD` | pale green · deutL 0.90 · 15.2:1 |
| `DiffAddedBg` | `#123A2A` | add-vs-remove 1.25 |
| `DiffRemovedBg` | `#3D1526` | magenta-shifted removed |
| `DiffAddedEmphasis` | `#6635C48C` | 0x66 tint of Success |
| `DiffRemovedEmphasis` | `#66FF6E8A` | 0x66 tint of Danger |
| `DiffWhitespaceMarker` | `#55E7B23A` | 0x55 tint of Warning |
| `BlameAgeNew` | `#E7B23A` | recent-edit heat |
| `BlameAgeOld` | `#2C3745` | old-edit heat (= hairline) |

Lanes: min pairwise deutL gap **0.101**, min contrast **4.33** — the cleanest CVD separation of the six
themes, by construction.

### 6.2 Draft `.axaml` (proposed, NOT wired — do not add to `ThemeManager.Themes`)

```xml
<!-- LOOM MERIDIAN — proposed CVD-first theme. PROPOSED, NOT WIRED.
     Cool high-contrast dark; lanes/diffs built on a lightness staircase so
     they stay orderable without hue. Do NOT merge into Themes/ or register in
     ThemeManager until §7 verification passes. Defines the full 32-token
     contract (AGENTS.md §Theming). -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Surfaces -->
    <SolidColorBrush x:Key="SurfaceWindow" Color="#0E1217"/>
    <SolidColorBrush x:Key="SurfacePanel" Color="#131A22"/>
    <SolidColorBrush x:Key="SurfaceDeep" Color="#090C10"/>
    <SolidColorBrush x:Key="SurfaceCard" Color="#1A2330"/>
    <SolidColorBrush x:Key="SurfaceHover" Color="#27313F"/>
    <SolidColorBrush x:Key="SurfaceHoverGhost" Color="#0027313F"/>
    <SolidColorBrush x:Key="ButtonBg" Color="#1F2A36"/>
    <SolidColorBrush x:Key="BorderHairline" Color="#2C3745"/>

    <!-- Text -->
    <SolidColorBrush x:Key="TextPrimary" Color="#EEF2F8"/>
    <SolidColorBrush x:Key="TextMuted" Color="#98A4B4"/>
    <SolidColorBrush x:Key="OnAccent" Color="#090C10"/>

    <!-- Signature accent -->
    <SolidColorBrush x:Key="AccentBrush" Color="#33B1E0"/>
    <SolidColorBrush x:Key="AccentHover" Color="#5CC4EC"/>
    <SolidColorBrush x:Key="AccentSelection" Color="#2633B1E0"/>

    <!-- Semantic -->
    <SolidColorBrush x:Key="SuccessBrush" Color="#35C48C"/>
    <SolidColorBrush x:Key="SuccessHover" Color="#57D3A3"/>
    <SolidColorBrush x:Key="DangerBrush" Color="#FF6E8A"/>
    <SolidColorBrush x:Key="DangerHover" Color="#FF8CA1"/>
    <SolidColorBrush x:Key="WarningBrush" Color="#E7B23A"/>
    <SolidColorBrush x:Key="InfoBrush" Color="#7C8CF5"/>

    <!-- Commit-graph lanes (lightness staircase; decoupled from semantics) -->
    <SolidColorBrush x:Key="Lane1" Color="#77C7E7"/>
    <SolidColorBrush x:Key="Lane2" Color="#9461ED"/>
    <SolidColorBrush x:Key="Lane3" Color="#EB5AA7"/>
    <SolidColorBrush x:Key="Lane4" Color="#EDB020"/>
    <SolidColorBrush x:Key="Lane5" Color="#DDF5DD"/>

    <!-- Diff line backgrounds (luminance-separated; removed magenta-shifted) -->
    <SolidColorBrush x:Key="DiffAddedBg" Color="#123A2A"/>
    <SolidColorBrush x:Key="DiffRemovedBg" Color="#3D1526"/>
    <SolidColorBrush x:Key="DiffAddedEmphasis" Color="#6635C48C"/>
    <SolidColorBrush x:Key="DiffRemovedEmphasis" Color="#66FF6E8A"/>
    <SolidColorBrush x:Key="DiffWhitespaceMarker" Color="#55E7B23A"/>

    <!-- Blame age-heat gutter -->
    <SolidColorBrush x:Key="BlameAgeNew" Color="#E7B23A"/>
    <SolidColorBrush x:Key="BlameAgeOld" Color="#2C3745"/>
</ResourceDictionary>
```

---

## 7. Verification plan — gates before any proposed value ships

No value here ships until it passes, in order:

1. **Full-token-coverage check.** Any retuned or new theme must define **all 32** tokens (§1). A token
   missing from one theme is an uncatchable runtime bug (AGENTS.md). A wiring change would also register
   Loom Meridian in `ThemeManager.Themes` and add its File → Theme item — out of scope here.
2. **Automated contrast + CVD gate (new unit test, `Mainguard.Tests`).** A pure test over the proposed
   palettes asserting, per theme: every lane ≥ 3:1 vs `SurfacePanel`; `TextPrimary` ≥ 4.5:1 and
   `TextMuted` ≥ 4.5:1 vs each surface it paints on; `OnAccent` ≥ 4.5:1 on Accent/Success/Danger fills;
   min pairwise **deutL** gap across the five lanes ≥ 0.07; diff add-vs-remove ≥ 1.15 and a
   deuteranopia-hue split (not collapsed). This encodes §2–§4 as CI, and re-flags the two ⚠ borderline
   lanes.
3. **Headless render harnesses (Skia offscreen), looped over all six themes.** Reuse the
   `AnalyticsRenderHarness` pattern (`ThemeManager.Apply(<key>, persist:false)` → build VM after the
   swap → `CaptureRenderedFrame().Save(...)` to `artifacts_headless/` for visual review):
   - **`GraphInteractionsRenderHarness`** — extend its theme loop to all six; capture a multi-lane DAG so
     lane separability is eyeballed in every theme (today's harnesses only loop Midnight + Daylight).
   - **`DiffQualityRenderHarness`** + **`DiffViewerFileRemovedRenderHarness`** — render add/remove
     (and intra-line emphasis + whitespace marker) per theme to confirm the retuned line backgrounds
     read distinctly and diff text stays legible over them.
   - **New `LaneSeparabilityRenderHarness`** (recommended) — render the five lanes side by side and emit
     both the normal PNG and a **deuteranopia-simulated** PNG so a reviewer sees the CVD result directly.
4. **Manual CVD spot-check.** Run the deuteranopia (and protanopia/tritanopia) simulated captures past a
   human reviewer for the graph and a representative diff, in Midnight, Daylight, Atelier (worst case),
   and Loom Meridian.

Only after 1–4 pass does a *separate, human-approved* change edit `Themes/*.axaml` (all six files
together for any shared-token move) — never from this doc.

---

## Self-gate

- [x] Every retuned value is a **role-named token** value (Lane*/Diff* only); no raw color introduced.
- [x] **Full-token-contract aware** — 32 tokens listed; proposals note all must be defined per theme.
- [x] Each change **justified by separability/contrast** with computed contrast + deutL figures.
- [x] Sixth theme is **complete** (all 32 tokens) and **labeled "proposed, not wired"** in prose and in
      the snippet header.
- [x] **No live `.axaml` edited**; `AccentBrush`/surfaces/text/semantics unchanged (One-Accent held).
- [x] **N-1 cited** for the new name; N-5, DESIGN.md Semantic-Not-Literal / One-Accent, PRODUCT.md P1 &
      Accessibility honored.
- [x] Two borderline lanes (Atelier L4, Aurora L5) surfaced honestly, not hidden, and routed to the gate.

---

## Addendum — the 2026-08 four-theme restyle

This document's five-theme analysis is preserved as history; the lineup it describes has since
changed. The 2026-08 restyle ("professional/sleek, macOS-leaning") made these changes:

- **Command Deck and Loom Aurora were retired.** Their Tailwind-400-family palette (neon teal /
  rose / amber on tinted surfaces) read as generic-AI-website rather than instrument — the
  anti-reference §1 warns about. `ThemeManager.LegacyKeyMap` migrates persisted keys
  (CommandDeck → Graphite, LoomAurora → MidnightLoom) and self-heals the store.
- **Graphite joined**: a macOS-native neutral-graphite dark with Apple-semantic-derived colors
  (systemBlue-tint accent, systemRed danger), the most restrained theme in the lineup.
- **Cross-theme retunes**: danger moved from the rose/pink family to true reds per theme
  (`#E5484D` / `#C13939` / `#FF453A` / `#D65A4A`), awaiting-review dropped its magenta/purple for
  muted violets, Daylight got its own light-background ANSI ramp (this doc's §5 flagged the shared
  ramp; the byte-identical block was a real Daylight legibility bug), and the Midnight accent
  nudged from `#8B8BF5` to `#8487F0`.
- **§7's shipping gate finally exists as code**: `Mainguard.Tests/ThemeContrastGateTests.cs`
  machine-checks key-set completeness, AA text contrast (incl. the selection composite), lane
  floor + pairwise deuteranopic-lightness staircase (Viénot 1999), and terminal legibility. Its
  first run caught five real defects — including lane pairs in *every* theme that collapse for a
  deuteranopic reader — all fixed by machine-solved lane ladders that keep each hue's identity.
