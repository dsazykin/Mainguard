# Fable Creative / Design / Analysis Bundle — 4 combined lanes (safeguard-clean)

Run at reset. These are scoped to DESIGN, VOICE, and ANALYSIS — Fable's differentiated strengths — and
deliberately free of attack/exploit/adversary framing (that framing is what trips the broad safeguards,
and it's Opus's job anyway). Keep everything here constructive, explanatory, and design-forward.

Sequencing: Lane A first (the design foundation). Lanes B and E both conform to A → start them once A's
token/icon system is written. Lanes C and D are independent → run in parallel with A/B/E from the start.
All lanes run autonomous: one subagent per part, a PHASE REPORT after each (what changed · self-gate vs
the goal · surfaces/tokens/types named), then continue. Halt only if a part fails its self-gate twice or a
choice contradicts DESIGN.md / PRODUCT.md / the Voice Bible.

═══════════════════════════════════════════════════════════════════════════════════════════
SHARED MASTER BRIEF — prepend to every lane
═══════════════════════════════════════════════════════════════════════════════════════════
You are a senior product designer, writer, and analyst working on Mainguard — a premium, natively-rendered
Git GUI (.NET 10, Avalonia) evolving into a multi-agent control center. Your job on these lanes is to
take work that is already good to EXCEPTIONAL: this is taste, judgment, and clarity work, where your edge
shows. Read FIRST: DESIGN.md, PRODUCT.md, AGENTS.md, README, docs/creative/Mainguard_Voice_And_Delight_Bible.md, and
docs/creative/*. Per-lane, also read the files that lane names.

Non-negotiables:
- ONE design system, FIVE switchable themes (Midnight Loom default, Daylight Loom light, Command Deck,
  Atelier, Loom Aurora) — never assume "dark"; every surface must read in all five. No raw colors: bind
  design tokens by semantic role via {DynamicResource}. Fixed shape/spacing/type scale (radius 6/8/12/999;
  spacing 4/5/8/10/15/20). Motion 120–150ms, no bounce. The "Precision Loom" north star: quiet, layered,
  one signature accent per view.
- Anti-references: never the templated VS-Code-extension / Electron-web-view look, never bland
  enterprise-SaaS card-grids or hero-metric scaffolding.
- Voice: conform to the Voice & Delight Bible — premium, precise, calm, destructive-safety-first.
- For DESIGN lanes (A, B): LOAD THE `impeccable` SKILL before designing anything.
- Ground every claim in a REAL surface, token, view, or type (name it). Where you touch code, verify with
  `dotnet build` and hand back a proposed commit message; do not commit/push or touch main (CLAUDE.md).

═══════════════════════════════════════════════════════════════════════════════════════════
LANE A — Design System & Visual Foundation  (impeccable; do this first)
Outputs: corrected Themes/*.axaml + App.axaml icon geometries + the design-system spec docs/design/DesignSystem.md (pure design — no code)
═══════════════════════════════════════════════════════════════════════════════════════════
Harden and elevate the foundation every surface sits on. Read the five Themes/*.axaml, App.axaml,
DESIGN.md, and the confirmed findings in docs/creative/PolishSpec.md + ThemeRefinement.md.
Part 1 — Token decoupling fix. CONFIRMED defect: the commit-graph Lane1–Lane5 tokens are hex-identical to
semantic tokens in ALL five themes (e.g. Midnight Lane4==WarningBrush==#E3B341, Lane5==InfoBrush==#58A6FF,
Lane1==AccentBrush==#8B8BF5; analogous in Daylight/CommandDeck/Atelier/LoomAurora) — a DESIGN.md §2
"Semantic-Not-Literal" violation where graph topology accidentally implies status. Retune Lane1–Lane5 in
every theme to a harmonious, mutually-distinct hue set that shares NO hex with any semantic token, stays
on-brand per theme, and keeps the lanes colorblind-distinguishable. Output the corrected axaml values.
Part 2 — State-encoding & iconography. CONFIRMED defect: the signing badge distinguishes untrusted vs bad
by COLOR ONLY (both use SignatureWarningIcon; only WarningBrush vs DangerBrush differs; no SignatureBadIcon
exists). Design an iconography system where meaning survives with zero color — a distinct glyph/shape per
signature state (verified / untrusted / bad) and an audit of every other color-only state encoding in the
app. Output the icon geometries (App.axaml) + where each binds.
Part 3 — Accessibility across all five themes. WCAG 2.1 AA contrast for text, diff add/remove, the new lane
hues, and badges; colorblind-safe distinctions throughout. Include the live-recolor fix: BlameView must
subscribe to ThemeManager.ThemeChanged (confirmed: it only subscribes to VisualLinesChanged, so a theme
switch leaves stale gutter colors) — mirror the CommitGraphCanvas pattern.
Part 4 — Motion & delight vocabulary. Which moments earn animation (rebase lands, conflict fully resolved,
clone completes, push succeeds, a hazard blocked) within 120–150ms/no-bounce, extending the loom/weave
metaphor. Concrete easing/timing storyboards Opus can implement.
NOTE: the three confirmed UI bugs (blame-gutter ThemeChanged, SignatureBadIcon, lane-token collisions) are
already being fixed in code SEPARATELY — do NOT re-implement them or edit the axaml (that would conflict).
This lane is PURE DESIGN, no code, no build: it supplies the DECISIONS those fixes should adopt — the exact
decoupled lane palette per theme, the SignatureBadIcon geometry + the full state-encoding icon set, and the
accessibility framework — as a design-system spec (docs/design/DesignSystem.md) the fixers apply. If the
in-flight patches picked arbitrary values, this lane is what upgrades them from "done" to "right."

═══════════════════════════════════════════════════════════════════════════════════════════
LANE B — Surface & Feature Design  (impeccable; conforms to Lane A; start once A's tokens/icons exist)
Output: docs/design/SurfaceDesigns.md + docs/design/FeatureDesigns.md
═══════════════════════════════════════════════════════════════════════════════════════════
Take the product's key surfaces and its planned features to a world-class standard, all on Lane A's
foundation. Read PRODUCT.md, the existing ViewModels/Views list in AGENTS.md, docs/creative/Onboarding.md,
and docs/planning/Mainguard_Backlog.md §A (the C1–C5 features).
Part 1 — Elevate existing surfaces. For each of: the review cockpit (the daily-driver reason to open
Mainguard), the commit graph, the diff viewer (side-by-side + unified), the staging panel, and the OOBE /
onboarding + "aha in 60 seconds" path — redesign for information hierarchy, reduced cognitive load, and
premium feel. Per surface: the IA, the primary flow, the empty/loading/error states, the one signature
accent, and how it reads across all five themes. Give described/ASCII mockups + the rationale + the tokens
and component classes used.
Part 2 — Design the new features (C1–C5) end-to-end as experiences, not just specs: the interactive bisect
assistant (Good/Bad/Skip wizard + progress + culprit card), global fuzzy search (grouped, highlighted,
keyboard-first overlay), the multi-repo dashboard + cross-repo "My Work" lane, the split-into-branches
wizard, and the client polish pack. Per feature: the flow, the surface, the keyboard-first interactions,
and the delight moment.

═══════════════════════════════════════════════════════════════════════════════════════════
LANE C — Voice, Copy & Narrative  (independent; run in parallel)
Output: an elevated Voice Bible + docs/creative/Microcopy.md (final strings) + docs/creative/Narrative.md
═══════════════════════════════════════════════════════════════════════════════════════════
The writing/persuasion lane — pure voice work. Read the Voice & Delight Bible, docs/creative/EmptyStates.md
+ Microcopy.md, docs/business/market-analysis/*, and docs/business/go-to-market/*.
Part 1 — Product voice & copy. Elevate the Voice Bible from good to exceptional (sharper principles,
better do/don't examples). Then APPLY it: re-derive and tighten every empty-state headline, microcopy
string, hostile-git-error rewrite (index.lock, detached HEAD, non-fast-forward, mid-rebase conflict),
destructive-safety confirmation, toast, and tooltip. Produce the string inventory: surface → current →
proposed final copy, all on-voice.
Part 2 — GTM / narrative / positioning. Deepen the market & launch layer: the competitor teardown (vs
GitKraken, Fork, Tower, Sublime Merge, Conductor and the orchestrator field — sourced from
docs/market-analysis), the positioning + enemy framing, pricing logic, the launch narrative (Show HN post,
the "why I built this" founder story), and a README hero rewrite. Persuasive, on-voice, evidence-backed.

═══════════════════════════════════════════════════════════════════════════════════════════
LANE D — Analysis & Explanation  (independent; run in parallel — KEEP FRAMING CONSTRUCTIVE)
Output: a deepened Uplift Study (methodology only) + an expanded Cloud/Vibe Companion + a cost model
═══════════════════════════════════════════════════════════════════════════════════════
Rigorous reasoning + clear exposition, for a skeptical reader and a non-expert reader respectively.
IMPORTANT SCOPE: frame everything as methodology, teaching, and business reasoning — do NOT write in
attack/exploit/adversary terms (that trips the model's broad safeguards and belongs on the other model).
Part 1 — Uplift Study methodology. Read docs/phase-2/Mainguard_Uplift_Study_Spec.md. Deepen ONLY the experimental
design and statistics — the causal-inference argument (why the ablation ladder isolates the pipeline, not
the model), the confusion-matrix framing, the McNemar/Wilcoxon/power rigor, the contamination controls,
and the pre-registration logic. Make the methodology bulletproof to a skeptical statistician. Do NOT touch
the planted-hazard / exfil / red-team sections — leave those exactly as they are.
Part 2 — Cloud/Vibe companion + cost model. Read docs/phase-2/Mainguard_Cloud_Vibe_Companion.md and master Waves 3–4.
Deepen the plain-language explainer (concepts glossary, "what runs where" topology, the de-risking
sequence) as TEACHING for someone with no cloud background — explanatory, never adversarial. Then build the
missing piece: a cost & scaling model — what a cloud session costs to run, how metering maps to price,
where margin lives, and the business case for the cloud tier.

═══════════════════════════════════════════════════════════════════════════════════════════
LANE E — Phase-2 Control Center & Vibe Mode: DESIGN + runnable prototype  (impeccable; conforms to Lane A)
Output: docs/design/ControlCenterDesign.md + docs/design/VibeModeDesign.md, then Avalonia Views +
ViewModels + mock-data services (a runnable, theme-switchable prototype)
═══════════════════════════════════════════════════════════════════════════════════════════
Make the swarm vision TANGIBLE. Read PRODUCT.md, AGENTS.md (MVVM/ViewLocator conventions + the existing
Views/ViewModels), DESIGN.md, Lane A's design-system output, and the specs that define these surfaces'
FUNCTION — control center: P2-13 (activity bar & docking), P2-11 (review cockpit), P2-10 (merge-queue
states), P2-14 (coordinator chat + plan approval + kill switch), P2-29 (session board), P2-39
(orchestration UX pack), P2-44 (sandbox health), P2-45 (flight recorder), P2-41 (remote dashboard); Vibe:
P3-02 (escalation triage), P3-03 (Vibe UI: mode toggle + chat + live preview), P3-04 (one-click deploy).
Read OPS §3.4/§4 for the agent-state / queue-state / event types the UI renders (render states — do NOT
discuss attacks; this is UI, not security).

BINDING CONSTRAINT: EXTEND the existing "Precision Loom" 5-theme system (Lane A's tokens/icons/motion) —
never a new aesthetic. Every color a token via {DynamicResource}; reads in all five themes; one signature
accent per view; 60fps; the anti-references (no Electron/VS-Code-extension look) apply doubly here.

Part 1 — Control Center design. The full swarm command surface: the Activity Bar (P2-13: resource monitor
sparklines + token-spend, the Coordinator tab with an attention pulse, a virtualized LIFO agent list with
status micro-badges) + the Dock.Avalonia workspace (per-agent terminal + diff + staging); the Coordinator
chat + TaskPlan approval card + the always-visible kill switch (P2-14); the merge-queue visualized as its
state machine + stale cascade + CanMerge gating (P2-10); the review cockpit (risk-ranked hunks, provenance
chips, the flagged-changes acknowledgment gate, the test-delta strip — P2-11); the session board (kanban +
side-by-side candidate comparison, P2-29); the sandbox-health + flight-recorder + remote-dashboard panels.
Per surface: IA, primary flow, the one accent, empty/loading/error states, delight moments, all five
themes. Described/ASCII mockups + rationale + the tokens/classes used.

Part 2 — Vibe Mode design. The zero-knowledge surface for non-technical founders — warm, jargon-free,
calm. The mode toggle (P3-03) collapsing the dev dock into the 2-pane Chat + LivePreview; the chat
rendering orchestrator events as friendly cards (checkpoint created / verifying / escalation — translate
the OPS §3.4 event types into human language); the LivePreview pane; the escalation triage screen (P3-02:
plain-language, EXACTLY three actions — "Try a different approach" / "Go back to when it worked" / "Get
help" — no raw stack traces, a "show technical details" expander, honest disabled states); one-click deploy
(P3-04: the "Publish to Web" flow + the live-URL card). Per surface: emotional design + copy (conform to
the Voice Bible), all five themes, the delight.

Part 3 — Build the runnable prototype. Implement the surfaces as real Avalonia Views + ViewModels in the
project's conventions (CommunityToolkit.Mvvm [ObservableProperty]/[RelayCommand], ViewLocator pairing,
tokens via {DynamicResource}, the Button.*/Border.* classes — no raw colors, no logic in code-behind).
Wire them to MOCK DATA SERVICES behind interfaces SHAPED LIKE the future gRPC contract: a fake agent list +
scripted AgentStatus transitions, a fake merge queue with scripted state changes + a stale cascade, a fake
event stream (OPS §3.4 shapes), a scripted coordinator chat, fake sandbox/health metrics. Design the
ViewModels so the mock service can later be swapped for DaemonClient with ZERO View changes — the prototype
becomes the real UI when P2-02 lands. Build INCREMENTALLY (shell → each panel → Vibe), keeping dotnet build
green and the app launchable at every step, and add headless render-harness PNGs of each new surface in all
five themes (the P2-13 test pattern). Do NOT build any real backend — no daemon, no Docker, no agents; the
mock services stand in. This is the one lane that ships runnable code, so verify each increment with a
build and a launch, and hand back a proposed commit message per increment (no commit/push, no touching
main).

═══════════════════════════════════════════════════════════════════════════════════════════
HOW TO LAUNCH — the exact messages to send (correct parallelisation baked in)
═══════════════════════════════════════════════════════════════════════════════════════════
This file lives at fable-creative-bundle.md (repo root), so every agent/subagent can read it and its lane
blocks directly. Dependency rule (non-negotiable): Lane A before Lanes B and E; Lanes C and D run from the
start. Output homes: design → docs/design/ · voice/writing → docs/creative/ · deepened specs → docs/phase-2/.

── RECOMMENDED: one orchestration message to a single lead Fable agent (it fans out the parallelism) ──

You are the orchestrator for the 5-lane creative/design/analysis bundle in fable-creative-bundle.md (repo
root). Read the whole file first — the SHARED MASTER BRIEF applies to every lane and each lane block is its
spec. Dispatch the lanes as SUBAGENTS with exactly this parallelisation:
- NOW, in parallel (independent, different files): spawn Lane A, Lane C, and Lane D.
- WHEN Lane A has written docs/design/DesignSystem.md: spawn Lane B  —  conforms
  to Lane A's design system and MUST NOT start before it exists.
Each subagent prepends the Master Brief, then executes ONLY its lane's block, autonomously — one subagent
per part, a PHASE REPORT after each part (what changed · self-gate vs the goal · surfaces/tokens/types
named), then continue. Halt a lane only if a part fails its self-gate twice or a choice contradicts
DESIGN.md / PRODUCT.md / the Voice Bible. Lane E is the only lane that writes runnable code — it verifies
each increment with `dotnet build` + a launch and hands back a proposed commit message per increment; no
lane commits, pushes, or touches main. Relay each lane's phase reports to me as they land, and tell me
immediately if any lane halts.

── ALTERNATIVE: isolated sessions (better if you want the big Lane E build on its own) ──

Open separate Fable sessions; in each, paste the SHARED MASTER BRIEF followed by the named lane block:
- Session 1 → Lane A. Fire now.
- Session 2 → Lane C. Fire now (independent).
- Session 3 → Lane D. Fire now (independent).
- Session 4 → Lane B, and Session 5 → Lane E. Start BOTH only once Lane A has written
  docs/design/DesignSystem.md (they build on it).
Same dependency either way: A before B and E; C and D from the start.
