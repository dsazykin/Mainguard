# Product

## Register

product

Primary surface is the Avalonia desktop app itself. Mainguard also has a brand/marketing surface (GTM plan, advisor pitch materials under `docs/go-to-market/`) that is out of scope for this file and should get its own register pass (`brand`) if/when that work is designed.

## Users

Software engineers and "engineering managers" who are shifting from writing code by hand to orchestrating one or more autonomous coding agents (Claude Code, AGY, OpenCode, etc.) against a shared git repository. Today, before the multi-agent features exist, the user is simply a developer doing normal git operations (stage, commit, branch, merge, resolve conflicts, review diffs) who wants a fast, precise, natively-rendered client instead of a web-wrapped one. Their context is high-focus, keyboard-and-mouse desktop work, often with high-stakes destructive operations (rebase, force-push, conflict resolution) where trust in the tool matters.

## Product Purpose

Mainguard is a premium, natively-rendered Git GUI (Avalonia + LibGit2Sharp) built to eventually become a multi-agent control center — a dashboard for running and reviewing several autonomous coding CLIs against one repo without them clobbering each other or the human's own work. Today it is a fully working single-user Git client (commit graph, staging/diffs, partial staging, conflict resolution, branch/tag/worktree management, four switchable themes). Success today looks like: git operations that are faster and less error-prone than the CLI or existing GUIs, with zero `.git/index.lock`-style footguns. Success for the roadmap looks like: a human comfortably supervising a swarm of agents from one screen without losing control of their working directory.

## Brand Personality

Premium & precise. The product should feel like a high-craft instrument built for serious engineering work — controlled, confident, engineered — not a hobby project or a themed wrapper around a web view. This shows up today in the tokenized 4-theme design system (Midnight Loom, Daylight Loom, Graphite, Atelier), the deliberate shape/spacing/typography system in AGENTS.md, and 60fps native rendering of the commit graph. As multi-agent features land, the same precision should extend to how agent activity, conflicts, and audit trails are presented — nothing should feel loose or ambiguous when the stakes are a human's working directory.

The brand metaphor is the guard: agents work outside the walls, and nothing reaches `main` without passing inspection at the gate. Play it as calm, disciplined protection — night watch, honor guard, lighthouse keeper — never carceral or militaristic, and never through security-vendor clichés (shields, padlocks, fortresses). The vocabulary that follows from it: the merge queue is **the Gate**, verified work is **cleared** or **turned back**, the kill switch is **stand down**, the review surface is **the watchtower view**.

## Anti-references

- **Generic Electron dev tools.** Avoid the templated "VS Code extension" / web-view-in-a-shell feel — sluggish, generic chrome, inconsistent native behavior. Mainguard's native Avalonia rendering and custom design system are precisely the differentiator to protect.
- **Corporate enterprise SaaS.** Avoid bland blue-and-gray enterprise dashboard aesthetics, generic card-grid layouts, and hero-metric/eyebrow scaffolding — none of that fits a precision engineering tool used by individual developers.

## Design Principles

1. **One design system, switchable palettes.** Shape language, spacing, and typography are fixed; only color tokens change per theme. Never assume a specific theme (e.g. "dark") when designing a surface — it must read correctly in all of them.
2. **No raw colors, no ad-hoc components.** Every color is a named token used by meaning; every recurring visual (button, pill, card) is a shared class, not an inline one-off. This is what keeps a 5-theme system from rotting.
3. **Precision over decoration.** Motion, depth, and iconography exist to clarify state (hover, focus, selection, drag) — never as decoration for its own sake. Subtle, fast (120–150ms), never bouncy.
4. **Destructive-safety first.** Anywhere the user can lose work (rebase, discard, force-push, conflict resolution) the UI should default to the safer, more legible path — this is the product's core promise ("the exact bug this app exists to prevent").
5. **Design for today's single-user tool, architect for tomorrow's swarm.** The roadmap (multi-agent orchestration, sandboxes, terminals) is not built yet — don't design speculative UI for it, but don't paint the current design system into a corner either.

## Accessibility & Inclusion

WCAG 2.1 AA as the baseline (contrast, keyboard navigation). No additional accessibility requirements have been specified beyond that; revisit this section if color-blind-safe commit-graph-lane / diff-color requirements surface later, since `Lane1`–`Lane5` and diff add/remove colors currently rely on hue distinction.
