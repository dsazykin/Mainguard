# Design Hub — the navigable design reference

**Start here for anything visual or interactive.** This hub indexes the design corpus (Lanes A, B, E, I of the 2026-07 design pass) as one reference: which document rules on what, in which order to read them, and where any given surface is specified. Files keep their own homes; this page is the map. (Consolidated 2026-07-12.)

## The authority ladder

1. [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) (repo root) — the constitution: token roles, the themes, register, brand personality. Everything below conforms to them.
2. [`DesignSystem.md`](DesignSystem.md) — **the foundation specification (law within this folder).** Lane-A rulings with computed verification records: the corrected commit-graph lane palette per theme (Part 1), the state-encoding icon gates + the signature/severity triads (Part 2), the accessibility gates and retunes (Part 3), and the motion vocabulary — six primitives, three grades, the earned-moment ledger (Part 4). Where any other design doc disagrees with it, DesignSystem wins.
3. Surface & feature specs — conform to DesignSystem:
   - [`SurfaceDesigns.md`](SurfaceDesigns.md) — the five shipped core surfaces elevated: review cockpit (`RepoDashboardView`), commit graph, diff viewer, staging panel, OOBE/onboarding. Per surface: IA, primary flow, empty/loading/error states, the one signature accent, all themes.
   - [`FeatureDesigns.md`](FeatureDesigns.md) — the client-parity features (P2-C1…C5) designed end-to-end as experiences: bisect assistant, global fuzzy search, multi-repo home + attention lane, split-into-branches wizard, polish pack. Appendix B carries the `ShortcutMap`/`ActionRegistry` keyboard additions.
   - [`ControlCenterDesign.md`](ControlCenterDesign.md) — the phase-2 swarm command surface (Lane E Part 1): activity bar (P2-13), merge-queue rail (P2-10), workspace dock (P2-13/39/44), coordinator + plan approval (P2-14), review cockpit (P2-11), session board (P2-29), telemetry panels (P2-44/45/41), and the §9 shared state/badge vocabulary. **§0 is the revision of record where it disagrees with §1.**
   - [`VibeModeDesign.md`](VibeModeDesign.md) — the zero-knowledge surface (Lane E Part 2): mode toggle, event-cards chat, escalation triage (exactly three actions), one-click deploy, live preview (P3-02/03/04).
4. [`ProductAndUX.md`](ProductAndUX.md) — the product/UX layer (Lane I): the post-C1–C5 delight backlog (I-1…I-10), the usability heuristic audit of the shipped client (S1–S3 findings pinned to views), the complete command-surface spec (extends T-18), and the T-22 analytics redesign with its computed color-vision validator record.
5. [`assets/AnalyticsRedesign.html`](assets/AnalyticsRedesign.html) — self-contained five-theme preview of the Part-4 analytics redesign.

Voice and strings are not ruled here: they belong to the [Brand & Voice Hub](../creative/README.md) (the Voice & Delight Bible and Microcopy.md). Implementation bindings live in the [Master Implementation Document v2](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md), which links back to these chapters per task.

## Where is each surface specified?

| Surface / concern | Primary spec | Also see |
|---|---|---|
| Lane palette, tokens, contrast gates | DesignSystem Part 1 + Part 3 | ThemeRefinement §4.2/§6/§7 (diff backgrounds, Loom Meridian, shipping gates) in `../creative/` |
| State icons (signature, severity, badges) | DesignSystem Part 2 | PolishSpec §1 (ratified there) |
| Motion & delight | DesignSystem Part 4 | `../creative/MotionPlaybook.md` (per-moment Avalonia briefs), Voice Bible M-rules |
| Review cockpit (shipped) | SurfaceDesigns §1 | ProductAndUX Part 2 findings |
| Commit graph | SurfaceDesigns §2 | ADR-003 (`../phase-2/ADRs.md`), ProductAndUX Part 2 |
| Diff viewer | SurfaceDesigns §3 | ADR-004 |
| Staging panel | SurfaceDesigns §4 | ProductAndUX Part 2 (S1 stash findings) |
| OOBE / onboarding | SurfaceDesigns §5 | `../creative/Onboarding.md` (sequenced storyboard) |
| Bisect / search / multi-repo / split-branches / polish (P2-C1…C5) | FeatureDesigns §1–5 | ProductAndUX Part 1 (adjacent backlog I-1…I-10) |
| Command palette + shortcuts | ProductAndUX Part 3 | FeatureDesigns Appendix B |
| Analytics (T-22) | ProductAndUX Part 4 | assets/AnalyticsRedesign.html, PolishSpec §3 |
| Control center (P2-10/11/13/14/29/39/41/44/45) | ControlCenterDesign §2–§9 | Master v2 task sections |
| Vibe mode (P3-02/03/04) | VibeModeDesign §1–§5 | Master v2 task sections |
| Empty states (layout/ES card) | `../creative/EmptyStates.md` | final strings: `../creative/Microcopy.md` §6 |
