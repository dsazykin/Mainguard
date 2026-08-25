---
name: Mainguard
description: A premium, natively-rendered Git GUI evolving into a multi-agent control center.
colors:
  surface-window: "#0F1115"
  surface-panel: "#14171C"
  surface-deep: "#0B0D10"
  surface-card: "#1A1E24"
  surface-hover: "#252B34"
  button-bg: "#1E232B"
  border-hairline: "#262B33"
  text-primary: "#E6E9EF"
  text-muted: "#8A93A6"
  on-accent: "#0B0D10"
  accent: "#8487F0"
  accent-hover: "#A5A5F8"
  accent-selection: "#268B8BF5"
  success: "#42B968"
  success-hover: "#5BCB7F"
  danger: "#E5484D"
  danger-hover: "#FA8C8C"
  warning: "#E3B341"
  info: "#58A6FF"
  lane-1: "#9F9FFC"
  lane-2: "#F472B6"
  lane-3: "#2DD4BF"
  lane-4: "#E3B341"
  lane-5: "#58A6FF"
  diff-added-bg: "#11271B"
  diff-removed-bg: "#33191E"
typography:
  hero:
    fontFamily: "Inter, SF Pro Text, Segoe UI Variable, Segoe UI, Cantarell, Noto Sans, sans-serif"
    fontSize: "24px"
    fontWeight: 600
  title:
    fontFamily: "Inter, SF Pro Text, Segoe UI Variable, Segoe UI, Cantarell, Noto Sans, sans-serif"
    fontSize: "16px"
    fontWeight: 600
  body:
    fontFamily: "Inter, SF Pro Text, Segoe UI Variable, Segoe UI, Cantarell, Noto Sans, sans-serif"
    fontSize: "12px"
    fontWeight: 400
  label:
    fontFamily: "Inter, SF Pro Text, Segoe UI Variable, Segoe UI, Cantarell, Noto Sans, sans-serif"
    fontSize: "10px"
    fontWeight: 400
  mono:
    fontFamily: "JetBrains Mono, Cascadia Mono, SF Mono, Consolas, DejaVu Sans Mono, monospace"
    fontSize: "12px"
    fontWeight: 400
rounded:
  sm: "6px"
  md: "8px"
  lg: "12px"
  pill: "999px"
spacing:
  xs: "4px"
  sm: "5px"
  md: "8px"
  lg: "10px"
  xl: "15px"
  xxl: "20px"
components:
  button-primary:
    backgroundColor: "{colors.button-bg}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.sm}"
  button-accent:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.sm}"
  button-accent-hover:
    backgroundColor: "{colors.accent-hover}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.sm}"
  button-danger:
    backgroundColor: "{colors.danger}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.sm}"
  button-pill:
    backgroundColor: "{colors.accent-selection}"
    textColor: "{colors.accent}"
    rounded: "{rounded.pill}"
    padding: "7px 1px"
  card:
    backgroundColor: "{colors.surface-panel}"
    rounded: "{rounded.lg}"
---

# Design System: Mainguard

## 1. Overview

**Creative North Star: "The Quiet Gatehouse"**

Mainguard is a native instrument for engineers doing high-stakes git work — staging, rebasing, resolving conflicts — and, in its next phase, standing watch over a fleet of autonomous coding agents from a single desk, where nothing lands on `main` unguarded. The system is deliberately quiet: layered charcoal surfaces, hairline dividers, and exactly one signature accent (indigo-violet) that appears only where the eye should land — the current branch, the primary action, the active selection. Everything else recedes into a controlled, engineered calm. Motion is fast and functional (120–150ms fades, no bounce), never decorative; depth comes from stacked flat surfaces and a soft shadow on floating overlays, not from gradients — and not from glassmorphism as decoration; the one sanctioned translucency is the opt-in macOS vibrancy chrome defined in §4.

This system explicitly rejects the templated "VS Code extension" look — generic web-view chrome bolted onto Electron — and the bland blue-and-gray enterprise SaaS dashboard. It equally rejects security-vendor iconography — no shields, padlocks, or fortress imagery; the guard reads as calm discipline, not armor. Mainguard is rendered natively at 60fps; its surfaces are tuned per pixel, not inherited from a component library default.

**Key Characteristics:**
- One design system, four interchangeable color themes (Midnight Loom default, Daylight Loom — light, Graphite — the macOS-native neutral graphite, Atelier) — shape, spacing, and typography never change between them. (Command Deck and Loom Aurora were retired in the 2026-08 restyle: their saturated palette read as generic-AI-website, not instrument.)
- A single signature accent color per theme, reserved for the one emphasized action or state per view.
- Flat, layered surfaces (window → panel → card) instead of shadows-as-decoration.
- Semantic color roles named by meaning (Success, Danger, Warning, Info), never referenced by literal hue in code.

## 2. Colors

The palette below is Midnight Loom, the default theme; three sibling themes (Daylight Loom — light, Graphite, Atelier) redefine every value below while keeping the same roles. No surface, text, or component may reference a hex value directly — only the named role.

### Primary
- **Signature Violet** (`#8487F0`, hover `#9EA1F5`): the one accent per screen — current branch, links, the single `Accent`-class button, focus rings, active selection tint (`#268487F0` at low opacity).

### Neutral
- **Void** (`#0F1115`): the window background every floating panel sits on.
- **Panel Charcoal** (`#14171C`): floating panel / sidebar card surface.
- **Deep Charcoal** (`#0B0D10`): the deepest surface — code and diff editor, and the text color painted *on top of* accent/success/danger fills.
- **Raised Charcoal** (`#1A1E24`): inputs, raised cards, segment tracks.
- **Hover Slate** (`#252B34`): hover state / neutral selection background.
- **Hairline** (`#262B33`): the only border color; 1px dividers and card outlines.
- **Ink** (`#E6E9EF`): primary body and title text.
- **Fog** (`#8A93A6`): muted metadata, hints, secondary text.

### Named Rules
**The One Accent Rule.** At most one `Button.Accent` per view. The signature violet marks the single emphasized action; everything else is neutral (`Button.Primary`), muted (`Button.Secondary`), or semantic (`Success`/`Danger`).

**The Semantic-Not-Literal Rule.** Success (`#42B968`), Danger (`#E5484D`), Warning (`#E3B341`), and Info (`#58A6FF`) are fixed roles used by meaning across every theme — destructive actions are always Danger, never an ad-hoc red. Commit-graph lanes (`Lane1`–`Lane5`) are deliberately decoupled from these semantic colors so graph topology never accidentally implies status.

## 3. Typography

**UI Font:** Inter (fallback: SF Pro Text, Segoe UI Variable, Segoe UI, Cantarell, Noto Sans, sans-serif) — applied globally to every window.
**Mono Font:** JetBrains Mono (fallback: Cascadia Mono, SF Mono, Consolas, DejaVu Sans Mono, monospace) — SHAs, diffs, code, anything fixed-width.

**Character:** A single humanist-geometric UI sans carries the entire interface at a compact, information-dense scale; mono is reserved strictly for content that *is* code or a hash, never for UI chrome.

### Hierarchy
- **Hero** (600 weight, 24px): the rare, largest moment in the app — empty-state headlines.
- **Title** (600 weight, 16–18px): panel and dialog titles.
- **Emphasis** (500–600 weight, 14px): emphasized inline text within body content.
- **Body** (400 weight, 12–13px): default control and body text throughout the app.
- **Label** (400 weight, 10–11px): metadata, chips, timestamps, and other secondary annotations.
- **Mono** (400 weight, 12px, `TextBlock.Mono`): SHAs, diff text, code.

### Named Rules
**The One-Family Rule.** Every window uses `FontUi` by default; only content that is literally code, a hash, or a diff line switches to `FontMono`. No third UI typeface, ever.

## 4. Elevation

Mainguard is flat by default and layered, not shadowed: depth comes from stepping between four fixed surface tones (Void → Panel → Card → Hover), not from box-shadow. The one exception is overlay chrome (command palette, confirmation dialogs), which floats above a full-bleed scrim and gets a single soft ambient shadow to separate it from the dimmed app behind it.

### Shadow Vocabulary
- **Scrim** (`#C0000000` full-bleed background): dims the app behind a modal overlay.
- **Overlay card shadow** (`0 10 30 0 #40000000`-family, literal per-overlay): the only place a drop shadow appears — a radius-12 card on `SurfacePanel` with a hairline border, floating above the scrim.

### Named Rules
**The Flat-By-Default Rule.** Panels, cards, and buttons at rest carry no shadow — separation comes from surface-tone stepping and hairline borders. Shadows appear only on transient overlay chrome, never on persistent layout.

**The Vibrancy Exception (macOS, opt-in).** Flat and opaque remains the default — and the canonical, harness-verified rendering on every platform. On macOS only, a user preference ("Translucent window chrome", off by default) may render the main window's OUTER chrome — the window background, title bar, and section rail — over the system vibrancy blur (`NSVisualEffectView` via Avalonia's AcrylicBlur transparency hint). The mechanics: those three paints bind the `ChromeWindowBackground`/`ChromePanelBackground` indirection tokens (opaque duplicates of `SurfaceWindow`/`SurfacePanel` in every theme), which `VibrancyManager` shadows with the theme's `SurfaceWindowVibrant`/`SurfacePanelVibrant` translucent variants only while the platform has actually granted the blur. Content surfaces — cards, diff, terminal, every reading surface — never route through the chrome tokens and are always opaque; contrast gates are evaluated against the opaque values. This is material, not decoration: no translucency anywhere else, and never as a substitute for surface-tone stepping.

## 5. Components

### Buttons
- **Shape:** radius 6 for all standard actions (`Primary`/`Accent`/`Success`/`Danger`/`Secondary`/`Segment`); radius 999 (pill) for `IconButton` hover and `Pill`; radius 0 for `WindowButton` (title-bar chrome).
- **Primary:** neutral fill (`ButtonBg` `#1E232B`), hairline border, `TextPrimary` text — the default action look.
- **Accent:** signature-violet fill, `OnAccent` (`#0B0D10`) text — reserved for the one emphasized CTA per view.
- **Success / Danger:** same shape as Accent, filled with the matching semantic color and `OnAccent` text.
- **Secondary:** transparent background, muted text, hairline border — cancel/dismiss actions.
- **Hover / Focus:** all buttons cross-fade their background over a 130ms `BrushTransition`; no per-view custom hover animation. Ghost-style buttons (IconButton, Secondary, Segment, WindowButton) rest on a zero-alpha copy of the hover color (`SurfaceHoverGhost`) rather than `Transparent`, so the hover fade never flashes white.
- **IconButton:** circular (radius 999) hover target, 6px padding, for toolbar/inline icon actions.
- **Pill:** capsule (radius 999) — the navbar branch selector and ref/branch chips.
- **Segment:** radius 6 buttons inside a radius-8 `SegmentTrack`, `12,3` padding — segmented switches (e.g. Commit/Shelf); never rendered as underline tabs.

### Chips / Pills
- **Style:** radius-999 borders filled with `AccentSelection` (translucent accent tint), text in `AccentBrush` — used for branch/ref chips and toast pills (`OnAccent` text on toasts).
- **State:** no separate "unselected" chip variant exists today; chips are inherently in an active/tagged state.

### Cards / Containers
- **Corner Style:** radius 12 for floating panel cards and overlay dialogs; radius 8 for inputs, small cards, and banners.
- **Background:** `SurfacePanel` for floating cards on `SurfaceWindow`; `SurfaceDeep` override for the diff/code editor card; inner panels within a card use `Transparent` since the parent card already provides the surface.
- **Shadow Strategy:** none at rest (see Elevation); overlay cards only.
- **Border:** 1px `BorderHairline` on every card.
- **Layout:** floating panels are separated by transparent 8px `GridSplitter` gutters, never border-fused grid cells.

### Inputs / Fields
- **Style:** radius 8, `SurfaceCard` background, hairline border.
- **Focus:** border shifts to `AccentBrush` on focus — a single global style, never redefined per view.

### Selection & Rows
- **Style:** selected rows get an `AccentSelection` background plus a 3px rounded `AccentBrush` rail on the left edge; the rail's column is always reserved in layout so selecting a row never shifts content.

### Signature Component: Commit Graph Canvas
A virtualized, vector-drawn DAG lane router rendered directly (not via a chart library) at 60fps, using round line caps (`PenLineCap.Round`) and the five decoupled `Lane1`–`Lane5` colors. It is the clearest expression of the system's "precision instrument" character — dense information rendered with calm, exact linework rather than a busy diagram.

## 6. Do's and Don'ts

### Do:
- **Do** bind every color through a named `{DynamicResource}` token, referenced by semantic role (e.g. `TextMuted`, `DangerBrush`), never by literal hue.
- **Do** keep shape, spacing, and typography identical across all themes — only color values change.
- **Do** limit each view to at most one `Button.Accent`; route destructive actions through `Button.Danger`, cancels through `Button.Secondary`.
- **Do** keep motion to 120–150ms opacity/brush transitions with no layout shift.
- **Do** verify any new UI reads correctly in both Midnight Loom (dark) and Daylight Loom (light) before shipping it.

### Don't:
- **Don't** hardcode a hex color or use `StaticResource` for anything that should follow a live theme switch — only `DynamicResource` tracks `ThemeManager`'s runtime swap.
- **Don't** build the generic "VS Code extension" look: no web-view chrome, no inconsistent native affordances standing in for a real desktop control.
- **Don't** reach for bland enterprise-SaaS blue-and-gray dashboards, generic card grids, or hero-metric/eyebrow scaffolding — none of that fits a precision engineering tool.
- **Don't** add a shadow to persistent, at-rest layout (panels, cards, buttons); shadows are reserved for transient overlay chrome only.
- **Don't** invent a new corner radius outside the fixed scale (6 / 8 / 12 / 999) or a new spacing value outside the 4/5/8/10/15/20 scale.
- **Don't** design speculative UI for the unbuilt multi-agent/sandbox/terminal roadmap — the current system serves today's single-user git client.
