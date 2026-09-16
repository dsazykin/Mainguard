# Mainguard — Polish Spec

Turning the polish backlog into precise, buildable taste specs. Every fix is a concrete
design-system move — a named token, a scale value, a component class, a motion brief, or a copy
line — pinned to a real view/control in the [AGENTS.md](../../AGENTS.md) Repository Map and cited
against [`DESIGN.md`](../../DESIGN.md), [`PRODUCT.md`](../../PRODUCT.md), and the
[Voice & Delight Bible](Mainguard_Voice_And_Delight_Bible.md) (rule IDs `V-#`/`E-#`/`C-#`/`T-#`/`TT-#`/`ES-#`/`M-#`/`N-#`).

**Source of the backlog.** This spec covers the polish section of
[`docs/planning/Mainguard_Backlog.md`](../planning/Mainguard_Backlog.md). The backlog labels it **§B-3 — Visual / feel
polish (cosmetic; non-blocking)** (there is no literal "B-3" heading beyond the table row; §B-3 is
that table under "B. Deferred within shipped features"). Its seven rows are specced below, in
descending taste-leverage order rather than backlog order.

**Scope / tense.** Every row of §B-3 targets a **shipped** single-user surface — none touches the
unbuilt multi-agent/sandbox/terminal UI, so nothing here is marked **[Horizon]**. Where a fix would
brush against future agent surfaces, it is called out and kept non-actionable. Per Design Principle 5
and DESIGN.md §6, no speculative agent UI is designed here.

**Design-system invariants (every fix preserves these).**
- No raw color; bind a named token via `{DynamicResource …}`, never `StaticResource`, for anything
  that must follow a live theme switch (AGENTS.md "golden rule"; DESIGN.md §6). Code-drawn colors
  resolve through `TryGetResource(..., ActualThemeVariant, …)` with a literal fallback **and**
  re-resolve on `ThemeManager.ThemeChanged` (the `CommitGraphCanvas` reference pattern).
- Radii only from `6 / 8 / 12 / 999`; spacing/padding only from `4 / 5 / 8 / 10 / 15 / 20`
  (DESIGN.md §6, AGENTS.md Shape system).
- Motion `120–150ms`, opacity/brush only, no bounce, no layout-affecting animation, honest-or-absent
  progress (`M-3`, `M-6`).
- At most one `Button.Accent` per view; destructive = `Button.Danger`, cancel = `Button.Secondary`
  (DESIGN.md One-Accent Rule).
- Semantic-not-literal: color by meaning; lanes stay decoupled from status; new tokens land in
  **every** `Themes/*.axaml` (DESIGN.md §2, `N-5`).
- Copy is precise, calm, engineered, and points to the way back (`V-1`,`V-2`,`V-3`,`V-5`,`V-6`).

---

## 1. Signing badge — glyph / size / placement (T-15) · **highest leverage**

**Backlog row:** "T-15 signing — signed-badge glyph/size/placement (crowds the message) · Guide §12.2."

**Surface.** `CommitTimelineView.axaml` lines 247–251 — the signature adornment inside the commit
row's message line, bound to `CommitRowViewModel` (`HasSignatureBadge`, `IsSignatureVerified` /
`IsSignatureUntrusted` / `IsSignatureBad`, `SignatureTooltip`). Icons `SignatureVerifiedIcon` /
`SignatureWarningIcon` live in `App.axaml`.

**Current gap (precise).**
1. The badge is a `13×13` `Panel` holding a `13×13` `PathIcon`. `13` is **off the icon scale**
   (AGENTS.md Icons: `14×14` inline, `10–12` adornments). A trust badge riding *inside* a message
   line is an adornment, so it should be `12×12`, not `13`.
2. **Untrusted and bad signatures render the same glyph** (`SignatureWarningIcon`), separated only by
   `WarningBrush` vs `DangerBrush`. That is a color-only distinction between two genuinely different
   states — it fails the "meaning must survive with zero color" bar (`M-7` spirit; PRODUCT.md
   accessibility) and blurs `V-6` "honest about the machine": *can't verify* (key not in keyring) and
   *bad* (contents don't match signature) must not read as the same thing.
3. Placement `Margin="0,0,5,0"` puts a `13px` block immediately left of the message with no reserved
   column, so a signed vs unsigned commit reflows the message start by 18px row-to-row — the "crowds
   the message" complaint.

**The fix (design-system moves).**
- **Size:** `12×12` for both the `Panel` and each `PathIcon` (icon-adornment scale).
- **Distinct glyphs, not distinct-color-only:** keep `SignatureVerifiedIcon` (a check/seal) in
  `SuccessBrush` for verified; keep `SignatureWarningIcon` (triangle) in `WarningBrush` for
  *untrusted*; add a **new distinct glyph** for *bad* — a "seal-broken" / error mark — in
  `DangerBrush`. Add it to `App.axaml` as `SignatureBadIcon` (StreamGeometry, theme-independent →
  `StaticResource`, per Icons rules); never inline path data. Now the three states differ by **shape
  first, color second**.
- **Reserve the column so nothing reflows:** wrap the badge in a fixed-width leading holder
  (`Width="12"`, `Margin="0,0,5,0"` — `5` = `sm`) that is always present in layout; toggle the
  glyph's `Opacity`/child visibility on `HasSignatureBadge` rather than collapsing the holder, so the
  message text starts at the same x for signed and unsigned rows (mirrors the DESIGN.md §5
  selection-rail "reserve the column" rule). `5` is the only spacing value used; drop the off-scale
  nothing-else.
- **Copy:** `SignatureTooltip` already follows `TT-3` (exact trust level per the three example
  strings in the Bible) — leave it; it is the correct model. No terminal period on the fragment.

**Acceptance criteria.**
- Both `PathIcon`s and the holder measure `12×12`; grep of the block shows no `13`.
- Verified / untrusted / bad each render a **visually different geometry**, confirmed in a greyscale
  screenshot (color removed) — the three are still distinguishable.
- Toggling a row between signed and unsigned does **not** shift the message-text start x (measure in
  the `SigningBadgeRenderHarness` frame: message left edge identical across a signed and an unsigned
  row).
- Tooltip strings unchanged and still name the exact trust level (`TT-3`, `V-6`).
- `SignatureBadIcon` exists in `App.axaml`; no inline geometry in the view.

---

## 2. Crowded PR-row action strip (T-29)

**Backlog row:** "T-29 checkout — crowded PR-row action strip · Guide §26.2."

**Surface.** `PullRequestsWindow.axaml` lines 98–111 — the per-PR action `StackPanel`
(`PullRequestRowViewModel`): `[Check out locally]` `[Review]` `[merge-method ComboBox]` `[Merge]`
`[Close]` `[eye IconButton]`.

**Current gap (precise).**
1. **Six controls in one horizontal strip**, all roughly equal weight — no visual hierarchy for
   "the main thing you do to a PR."
2. **Two `Button.Primary` side by side** (`Check out locally`, `Review`) compete for the same neutral
   emphasis; the eye reads no primary path (DESIGN.md One-Accent discipline — even absent an Accent,
   two identical neutral CTAs blur the hierarchy).
3. Button padding is `Padding="12,4"`. `12` and `4` — `4` is on-scale (`xs`) but `12` is **off the
   spacing scale** (`4/5/8/10/15/20`).
4. Long inline label `"Check out locally"` + full-word `Merge`/`Close` widen every row, so on a
   narrow window the strip wraps or clips.

**The fix (design-system moves).**
- **Collapse to a role-ranked trio + overflow.** Keep the row's genuine primary action —
  **`Merge`** (`Button.Success`) — plus its `merge-method` `ComboBox` inline. Move `Review`,
  `Check out locally`, `Close`, and `Open in browser` into a single trailing **overflow**
  `Button.IconButton` (a "…" kebab; ghost, resting on `SurfaceHoverGhost`, radius `999`, padding `6`)
  that opens a `MenuFlyout`. `Close` stays styled/labelled as the destructive path
  (`Button.Danger` semantics carried by a `DangerBrush` menu item), never demoted to an ambiguous
  "OK" (`C-3`).
- **Result:** at most three controls visible at rest — `[merge-method] [Merge] […]` — which reads as
  one clear action with a method and an overflow, matching the "precision instrument, not a toolbar
  dump" character (PRODUCT.md; DESIGN.md §6 "don't build the generic extension look").
- **Snap padding to scale:** the visible buttons use `Padding="10,5"` (`10`=`lg`, `5`=`sm`); the
  IconButton keeps its class padding `6`. Strip `Spacing` stays `8` (`md`). No `12`.
- **Tooltips carry the demoted labels** (`TT-1`): each overflow menu item keeps its full phrase
  ("Fetch this PR into a separate worktree…", "Read reviews & inline comments…"), so nothing is lost
  to iconography.
- **Disabled states as fixes** (`TT-2`): `Merge` disabled on a draft/unmergeable PR gets a tooltip
  stating the exact condition ("This PR is a draft — mark it ready before merging").

**Acceptance criteria.**
- At rest the strip shows exactly `[merge-method ComboBox] [Merge] [overflow …]`; the other four
  actions live in the flyout.
- No two `Button.Primary` sit adjacent; the destructive `Close` reads as `DangerBrush` in the menu.
- Every visible button padding is a scale value; grep shows no `Padding="12` in the row template.
- The full row fits without wrapping/clipping at the window's minimum width (verify in
  `PullRequestsRenderHarness`, including the T-29 checked-out banner state).
- Overflow items carry the previous full-text tooltips; the "check out locally" worktree explanation
  survives verbatim.

---

## 3. Analytics chart legibility in CommandDeck / Atelier / LoomAurora (T-22)

**Backlog row:** "T-22 analytics — chart legibility in CommandDeck / Atelier / LoomAurora (lane-hue
lightness overlap) · Guide §19.2."

**Surface.** `AnalyticsView.axaml` + `AnalyticsViewModel` (four LiveChartsCore charts) fed by
`Charts/ChartTheme.cs` — specifically `CategoricalPalette()` (the language-breakdown donut + bars).

**Current gap (precise).** The categorical palette *is* the semantics-free `Lane1`–`Lane5` tokens
(correctly — those are the only categorical hues the system owns; inventing chart hues is forbidden,
DESIGN.md §2). But in three themes the lanes cluster in lightness/hue so small marks (thin donut
slices, short bars) blur:
- **Atelier:** all five lanes are desaturated mid-lightness (`#D8A25A` tan, `#C08BB8` mauve,
  `#8FBC7F` sage, `#7FA6C4` dusty-blue, `#C4766B` terracotta) — tan vs terracotta especially merge.
- **CommandDeck:** `Lane1 #2DD4BF` teal and `Lane3 #34D399` green are near-identical hue+lightness.
- **LoomAurora:** `Lane1 #4FD1C5` teal and `Lane5 #60A5FA` blue sit close.
`ChartTheme` already reorders to `[Lane1, Lane2, Lane4, Lane3, Lane5]` so no confusable pair is
*adjacent* in draw order — good, but adjacency in the legend is not the same as separability of two
small slices on opposite sides of a donut.

**The fix (design-system moves — redundant encoding, never new hues).**
- **Never rely on color alone** (PRODUCT.md accessibility; dataviz discipline). Add non-color
  identity to the donut/bars:
  - **Hairline slice separators:** stroke each donut slice with a `1px` `BorderHairline` gap so two
    muted Atelier slices separate by an edge, not by hue. LiveCharts `Stroke` paint resolved via
    `ChartTheme.Color("BorderHairline", …)`.
  - **Direct labels:** show the language name + percent as a data label on each slice/bar (already
    theme-token text via `TextPrimary`/`TextMuted`), so identity is carried by **text**, and the
    legend swatch becomes confirmation, not the sole key.
- **Contrast gate against the surface:** each lane used in the donut must clear ≥ `3:1` against
  `SurfaceCard` (the card the chart sits on) in all five themes; where a lane fails (light lanes on
  Daylight, muted lanes on Atelier), the hairline stroke supplies the needed edge contrast rather
  than retuning the token (retuning a lane would ripple into the commit graph — out of scope here).
- **Punch-card + churn are already correct:** the sequential heat ramp blends `AccentBrush`→
  `SurfaceCard` (single-hue, legible light/dark) and churn uses `Success`/`Danger` by meaning — leave
  them; this fix is scoped to the categorical donut/bars.

**Acceptance criteria.**
- Donut slices carry a `1px` `BorderHairline` stroke and a direct name+percent label in all five
  themes.
- In `AnalyticsRenderHarness` (already captures Midnight + Daylight), **add CommandDeck, Atelier,
  and LoomAurora captures**; every language slice is distinguishable by label alone with color
  removed.
- No hex is introduced in `ChartTheme.cs`; every paint still resolves from a token (or the documented
  literal fallback).
- Each donut lane clears ≥ 3:1 against `SurfaceCard`, or is separated by the hairline stroke — verify
  with the dataviz palette checker referenced in `ChartTheme`'s own doc comment.

---

## 4. Review verdict picker shows raw enum names (T-25) · **quick win**

**Backlog row:** "T-25 PR review — verdict picker shows raw enum names (Comment/Approve/RequestChanges)
· Guide §22.2."

**Surface.** `PullRequestsWindow.axaml` line 241 — the submit-review `ComboBox`
`ItemsSource="{Binding Verdicts}"`, where `PullRequestsViewModel.Verdicts` is
`ReviewVerdict[] { Comment, Approve, RequestChanges }` (enum values rendered by `ToString()`).

**Current gap (precise).** `RequestChanges` shows to the user as the PascalCase identifier
`RequestChanges` — an implementation name leaking into the UI. This breaks `V-1` (precise, human
object language), `N-3` (name what is true, in the product's vocabulary), and `N-6` (one term per
concept, correctly cased).

**The fix (design-system moves).**
- **Reuse the existing display-string pattern.** `ReviewRowViewModel.VerdictText` (VM line 639)
  already maps `ReviewState`→human label ("Changes requested", "Commented"). Mirror it for the
  submit side: expose verdict *options* as label/value pairs (or a lightweight
  `ReviewVerdict`→display converter in `Converters/`), so the ComboBox renders:
  - `Comment` → **"Comment"**
  - `Approve` → **"Approve"**
  - `RequestChanges` → **"Request changes"**
- Sentence-style label ("Request changes"), matching `VerdictText`'s "Changes requested" register
  (`N-6` casing). The bound value stays the enum — display only changes, no behavior change.
- **Sibling smell, same fix:** the per-row `merge-method` `ComboBox` (line 103) binds the
  `PullRequestMergeMethod` enum directly — apply the same display mapping ("Merge", "Squash",
  "Rebase") so no raw enum name reaches any picker in this window.

**Acceptance criteria.**
- The verdict ComboBox items read "Comment" / "Approve" / "Request changes"; no PascalCase identifier
  appears in the rendered frame (`PullRequestsRenderHarness` T-25 state).
- The merge-method picker reads human labels.
- Selecting an item still binds the underlying enum (submit-review behavior + the "body required
  unless Approve" gate unchanged).

---

## 5. Clone progress-bar animation easing (T-21)

**Backlog row:** "T-21 clone — progress-bar animation easing · Guide §18.3."

**Surface.** `CloneDashboardView.axaml` lines 111–125 — the clone-progress overlay
(`ProgressBar Value="{Binding CloneProgressPercent}"`), driven by `CloneDashboardViewModel`
(`IsCloning`, `CloneProgressPercent`, `CloneStatusText`, `CloneErrorText`). Backed by
`ICloneService.CloneAsync`, whose `CloneProgress.Percent` is **monotonic by contract**.

**Current gap (precise).** `Value` snaps instantly to each reported percent (the `TODO(T-21
human-review)` note calls this out), so a fast clone reads as a jerky staircase — off-brand for a
"precision instrument" (PRODUCT.md), even though the underlying number is honest.

**The fix (design-system moves).**
- **Ease the fill, keep it honest.** Add a `Transitions` entry on the `ProgressBar`:
  `DoubleTransition` on `Value`, **`Duration=0:0:0.13`** (130ms, inside the `M-3` budget),
  `Easing=LinearEasing` (or a plain ease-out) — **no overshoot, no spring** (`M-3`, the "taut and
  settles, no bounce" loom metaphor). The transition animates only the indicator's own paint, not app
  layout, so `M-3`'s "no layout-affecting animation" holds.
- **Monotonic guarantee stays** (`M-6`): because `CloneProgress.Percent` never decreases, the eased
  value only ever moves forward — the easing smooths *between* honest samples, it never invents or
  rewinds progress.
- **Completion delight, within budget** (`M-1`, `T-1`): when `CloneProgressPercent` resolves to 100
  and the overlay closes, fade in a single `T`-pattern pill — `Cloned <repo>.` — over ~140ms and let
  it rest (`M-1` names clone completion as an earned-delight moment; the honesty of the monotonic bar
  reaching 100 *is* the celebration — a fade, never a flourish).
- **Leave the scrim/card as-is:** the overlay `#C0000000` scrim is an allowed literal (AGENTS.md
  exceptions); ensure the inner card is the standard radius-`12` `SurfacePanel` with the one soft
  `BoxShadow` (Depth & motion) — add it if the card currently rests flat.

**Acceptance criteria.**
- The `ProgressBar` has a `DoubleTransition` on `Value` at `130ms`, linear/ease-out, no overshoot.
- Driving a scripted monotonic sequence (0→30→30→80→100) shows the fill gliding forward only, never
  backward, never past 100 (`M-6`).
- On completion a single `Cloned <name>.` pill fades in ≤ 150ms and auto-dismisses (`T-1`, `M-1`);
  no confetti, no scale-pop (`M-5`).
- The overlay card is radius-12 `SurfacePanel` + hairline + soft shadow over the `#C0000000` scrim.

---

## 6. Blame gutter metrics + heat-ramp/contrast + live theme recolor (T-11)

**Backlog row:** "T-11 blame — gutter metrics + heat-ramp/contrast across 5 themes; live recolor on
theme switch · Guide §8.2."

**Surface.** `BlameView.axaml.cs` — the `BlameGutterMargin` custom `AbstractMargin` (age-heat bar +
`author · shortSha · relative-date`, boundary shading, click-to-select). Colors resolve at render
time via `ResolveColor`/`ResolveBrush` against `BlameAgeNew`/`BlameAgeOld`/`TextMuted`/`SurfaceHover`.
The `TODO(T-11 human-review)` comment scopes exactly this pass.

**Current gap (precise).**
1. **Metrics off-scale / oversized.** `GutterWidth = 232` crowds a narrow editor, and the label is
   drawn at `Point(HeatBarWidth + 6, …)` — that `6px` inset is **off the spacing scale**
   (`4/5/8/10/15/20`). `HeatBarWidth = 4` (`xs`) and font `11` (label scale) are already correct.
2. **No live recolor on theme switch.** The gutter resolves tokens at render time and repaints on
   `VisualLinesChanged`, but it does **not** subscribe to `ThemeManager.ThemeChanged`. Switch theme
   while a blame view is open and the gutter keeps the old heat/ text colors until the next scroll —
   violating the "long-lived visuals re-resolve on `ThemeChanged`" rule (AGENTS.md; the
   `CommitGraphCanvas` reference pattern).
3. **Heat-ramp contrast unverified across the five themes.** The bar lerps `BlameAgeOld`→
   `BlameAgeNew`; on Daylight (`BlameAgeOld #B9C0CC` light grey, `BlameAgeNew #C77D2E`) the *old* end
   risks washing out against a light editor surface; the ramp legibility is asserted nowhere.

**The fix (design-system moves).**
- **Metrics:** reduce `GutterWidth` to a tighter fixed value (target ~`200`, tuned so
  `author(16ch) · sha7 · rel-date` fits at font `11` without truncation-by-default) and change the
  label inset from `HeatBarWidth + 6` to `HeatBarWidth + 8` (`8` = `md`) so every internal offset is
  a scale value (`4` bar + `8` gap). Keep the bar at `4`, font `11`.
- **Live recolor:** subscribe to `ThemeManager.ThemeChanged` in `OnTextViewChanged` (attach) /
  detach on teardown → `InvalidateVisual()`, exactly like `CommitGraphCanvas`. Tokens already resolve
  per-render, so an invalidate is sufficient; no cached brushes to rebuild.
- **Heat-ramp contrast:** the two ramp endpoints are already tokens in all five `Themes/*.axaml`
  (`BlameAgeNew`/`BlameAgeOld` confirmed present) — this is a **verification + tuning** step, not new
  tokens: confirm the `old` endpoint clears ≥ `3:1` against `SurfaceDeep` (the editor surface) in
  each theme; the only bar that must never vanish is the oldest line. If Daylight's `#B9C0CC` fails,
  darken that one token in `DaylightLoom.axaml` (a token retune inside the existing contract, not a
  raw color in the view).
- **Copy:** the row is `author · shortSha · relative-date` and the tooltip is `sha \n summary` —
  refs/shas render mono (`N-6`); leave the format, it is already in-system.

**Acceptance criteria.**
- `GutterWidth` tightened; the label inset is a scale value (`4 + 8`); grep of the file shows no `6`
  offset and no `232`.
- Switching themes with a blame view open repaints the gutter's heat bar + text **immediately** (no
  scroll needed) — observable in `BlameRenderHarness` by capturing, switching variant, recapturing.
- The oldest-line heat bar clears ≥ 3:1 against `SurfaceDeep` in all five themes (spot-check Daylight
  + Atelier, the two lightest/most-muted).
- No hex in `BlameView.axaml.cs` except the documented fallback literals in the resolvers.

---

## 7. Per-section theme sweeps (all)

**Backlog row:** "all — per-section theme sweeps · throughout."

**Surface.** Every shipped View, but concretely the `Headless/*RenderHarness.cs` suite that already
captures rendered frames (analytics, signing badge, PR panel, issues, checks, notifications,
releases, reflog, operation history, blame, command palette, etc.).

**Current gap (precise).** Most harnesses capture **Midnight Loom only** (Analytics is the exception
— it already captures Midnight + Daylight). The three "hard" themes — **CommandDeck, Atelier,
LoomAurora** — plus **Daylight** (the one light theme, where "assumed dark" bugs surface) are not
systematically swept, so token drift (a hairline vanishing on Daylight, a muted chip fading on
Atelier) can ship unseen.

**The fix (design-system move — a repeatable process, not a one-off).**
- **Adopt the Analytics harness pattern as the standard:** each render harness captures the surface
  in **Midnight + Daylight** at minimum, and any surface with color-coded status (badges, chips,
  heat, lanes) additionally in **CommandDeck, Atelier, LoomAurora**.
- **Per-surface checklist** (the "before you finish a UI change" list, applied retroactively): no
  hardcoded hex; every color a `DynamicResource` token; hairlines visible on the light theme; badges
  distinguishable by shape + label (not color alone, per item #1); radii ∈ `{6,8,12,999}`; spacing ∈
  `{4,5,8,10,15,20}`.
- This item is a **standing discipline**, so its "done" is a green checklist per section, not a
  single diff.

**Acceptance criteria.**
- Every status-bearing surface has a render-harness capture in Daylight **and** the three flagged
  themes (parity with `AnalyticsRenderHarness`).
- A grep sweep of `Views/` + `Controls/` for `#`-hex and `StaticResource` on `Foreground`/
  `Background`/`Fill` returns only the documented exceptions (scrims, picker swatches, resolver
  fallbacks).
- No off-scale radius or padding in the swept views.

---

## Prioritized punch-list — premium-feel per unit effort

Ordered by taste-leverage ÷ effort. Do the top three first; they are the ones a discerning user
notices immediately.

| # | Item | Effort | Why it earns its place |
|---|------|--------|------------------------|
| 1 | **Verdict picker human labels** (§4) | XS | A raw `RequestChanges` in the UI screams "unfinished." One converter, reuses an existing pattern, kills an obvious leak. Ship first. |
| 2 | **Signing badge glyph/size/placement** (§1) | S–M | Trust signalling is core to a *precision, destructive-safety* tool; a shape-distinct verified/untrusted/bad badge that doesn't reflow the message is high-visibility craft. Also fixes a color-only-distinction accessibility gap. |
| 3 | **PR-row action strip** (§2) | M | Six competing buttons is the most obviously "toolbar-dump" surface in the app; collapsing to `[method] [Merge] […]` instantly reads more premium and fixes the two-Primary hierarchy blur. |
| 4 | **Clone progress easing + completion pill** (§5) | S | A 130ms `Value` transition + a `T-1` completion pill turns a jerky staircase into the honest, settling motion the loom metaphor promises — cheap, delightful, on-budget. |
| 5 | **Analytics categorical legibility** (§3) | M | Hairline slice strokes + direct labels make three themes' charts readable without touching a single hue — redundant-encoding done right. |
| 6 | **Blame gutter metrics + live recolor** (§6) | M | Lower visibility (blame is a dialog), but the live-theme-switch bug is a correctness gap against the golden rule; fold in the metric tidy while there. |
| 7 | **Per-section theme sweeps** (§7) | L (ongoing) | The insurance policy: catches the drift the other six could reintroduce. A standing discipline, not a sprint. |

**Effort key:** XS ≈ one converter/binding · S ≈ one control + a transition · M ≈ a control regroup
or a harness pass · L ≈ cross-cutting process.

---

## What this spec deliberately does not touch

- **No new hues.** The Atelier/CommandDeck/LoomAurora legibility fix (§3) uses form + label
  redundancy and existing tokens, never an invented chart color (DESIGN.md §2).
- **No agent/sandbox/terminal UI.** Every §B-3 row is a shipped single-user surface; nothing is
  `[Horizon]`. Future agent-status badges/toasts will *inherit* the shape-distinct-not-color-only
  badge rule from §1 and the human-label rule from §4, but that work is out of scope here
  (Design Principle 5).
- **No lane-token retune** (§6 aside): darkening `DaylightLoom`'s `BlameAgeOld` is a heat token, not
  a lane; the `Lane1`–`Lane5` values stay fixed so the commit graph is unaffected.
