# Onboarding — First Run & the 60-Second Aha

**The first launch of the shipped Mainguard client, storyboarded as a precision instrument — not a wizard.**

This is a Wave-2 creative doc. It conforms to the source-of-truth hierarchy: [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) govern the design system, register, and success definition; [`AGENTS.md`](../../AGENTS.md) pins every surface below to a real view, control, or ViewModel; the [Voice & Delight Bible](../Mainguard_Voice_And_Delight_Bible.md) supplies the numbered, citable rules (`V-#`/`ES-#`/`T-#`/`TT-#`/`M-#`/`N-#`). Where any of them disagree, they win and the drift is fixed here. Nothing here invents a color, radius, spacing value, or motion outside the system.

**Scope of tense.** Everything in §1–§4 governs the shipped single-user Git client (the code today). §5 is marked **[Horizon]** and is deliberately non-actionable — it records the *stance* future agent onboarding inherits, per Design Principle 5 and DESIGN.md §6 ("Don't design speculative UI").

**Sibling doc.** The empty-state copy and art referenced here are owned by the EmptyStates creative doc (`docs/creative/EmptyStates.md`), which conforms to the same `ES-#` rules. This doc *drives to and through* those states as a sequence; it does not re-specify them.

---

## 0. North Star for the first run

A serious engineer opening a new Git client is auditing it, not being taught. The first run must earn trust the way the product earns it everywhere else — by getting out of the way. Mainguard's onboarding is therefore **the absence of onboarding**: no modal tour, no coach-marks, no checklist. The first launch is a single honest empty state, one obvious action, and then the real product — rendered so cleanly and so fast that the craft is the welcome.

The whole design pivots on one line from PRODUCT.md: success today is *"git operations that are faster and less error-prone than the CLI or existing GUIs, with zero `.git/index.lock`-style footguns."* The first run exists to make that provable inside a minute (§3).

---

## 1. First-run storyboard

Every row names a real surface from the `docs/repo-map/` index, the copy (with its Bible rule), and the motion (restrained, within the M-3 budget). No cell is blank.

| # | Step | Surface (real view/control) | What the user sees / does | Copy (rule) | Motion (rule) | Aha beat |
|---|---|---|---|---|---|---|
| 1 | **Cold launch** | `MainWindow` shell hosting the `CloneDashboardView` empty state | The window paints its layered charcoal shell (Midnight Loom default) with one calm empty state centered — no dialog stacked on top, no tour scrim | Hero `No repository open` · Body `Open a folder that's a Git repo, or clone one from a remote.` (**ES-1**, **V-1**) | Window content fades in once at ~130ms opacity; no staggered reveal (**M-3**) | — |
| 2 | **The one choice** | Same empty state | Exactly one emphasized action plus one quiet alternative — the fork is legible in a glance, nothing is preselected or nagged | `Button.Accent` "Open repository" · `Button.Secondary` "Clone from a remote" (**ES-2** — the single allowed accent) | Button hover cross-fades over 130ms `BrushTransition`; no other motion (**M-3**) | — |
| 3 | **Clone form** | `CloneDashboardView` + `CloneDashboardViewModel` (T-21) | User pastes a remote URL and picks a target folder; a private-HTTPS host with no token routes to that host's sign-in rather than failing blind | Field label `Remote URL` · disabled-primary tooltip `Enter a remote URL and choose an empty folder to clone into` (**TT-2**, **V-5**) | Field focus shifts the border to `AccentBrush` — one global style, no per-view flourish (**M-3**) | — |
| 4 | **Honest progress** | `CloneDashboardViewModel` clone state (`IsCloning` / `CloneProgressPercent` / `CloneStatusText`) over `ICloneService` → the monotonic `CloneProgress` contract | A single progress bar advances monotonically (receive weighted 0–90%, checkout 90–100%); the status line names the real phase; a `Button.Secondary` "Cancel" is always present | Status `Receiving objects — 12,480 of 18,006` → `Checking out files` (**V-1**, **V-6** — honest phases) · cancel affordance (**V-4**, **V-5**) | Bar fills monotonically; no fake jumps, no indeterminate theatrics — honesty *is* the motion (**M-6**) | — |
| 5 | **Clone lands** | `CloneDashboardView` → workspace swap into `RepoDashboardView` | The bar resolves cleanly to 100%, a single confirmation pill fades in, and the workspace takes over | Toast pill `Cloned <repo> into <folder>.` (**T-1**, **V-1**) | Bar reaches 100%, then one ~140ms fade-in `T`-pattern pill — the whole celebration (**M-1**) | Setup is *over*, and it never felt like setup |
| 6 | **The workspace paints — THE AHA** | `RepoDashboardView` (layout host) → `CommitGraphCanvas` (Controls), `StagingPanelView`, `CommitTimelineView` | The full instrument appears *composed*: the vector-drawn DAG in `CommitGraphCanvas` renders the entire history at 60fps with round-capped lanes, staging and timeline docked beside it. No spinner-to-content jank, no per-row draw-in | No copy — the render speaks; refs render in `TextBlock.Mono`, current branch carries the one accent (**N-6**, DESIGN.md One-Accent Rule) | Graph appears fully composed — **no** entrance animation, **no** staggered lane draw; motion here is scroll, not choreography (**M-2**) | **The 60fps graph is the welcome.** A CLI can't show you this; a web-wrapped GUI can't show it this fast |
| 7 | **First navigation** | `CommitGraphCanvas` + `CommitTimelineView` / `CommitRowViewModel` | User hovers a row (a signature/stale-fetch tooltip explains a non-obvious state), then clicks a commit; the row selects without shifting a pixel | Tooltip e.g. `Verified — signed by daniel@… with a key in your keyring` (**TT-1**, **TT-3**) | `AccentSelection` fill + 3px `AccentBrush` rail fade in over the reserved rail column — **nothing shifts** (**M-2**, DESIGN.md §5) | The instrument responds like a native app, because it is one |
| 8 | **First change appears** | `StagingPanelView` → `DiffViewerView` | User edits a file in their editor; the `RepositoryWatcher`-driven refresh surfaces it in the staging panel without a manual reload; they stage a single hunk (partial staging) | Section headers `Staged` / `Unstaged` — plain nouns, one term per concept (**N-6**, **V-3**) | The list updates in place on the watcher event; selection/scroll unaffected (**M-2**) | Live, precise, no `git status` in a terminal |
| 9 | **First clean commit** | `StagingPanelView`'s `CommitComposerView` (T-31) + embedded `PreCommitFindingsView` (T-30); `Ctrl+Enter` from the default `ShortcutMap` | User types a subject, sees the live char counter, and commits with `Ctrl+Enter`. The pre-commit scan runs and reports all-clear; the working tree returns to clean | Pre-commit all-clear `Nothing risky staged` (owned by EmptyStates, **ES-4**, **V-2**) · clean-tree affirmation `Working tree clean` (**ES-4**, **V-3**) | Pre-commit panel shows the all-clear state (fade only); clean-tree affirmation is a quiet fade, not a fanfare (**M-1**, **M-2**) | The commit lands with a keystroke — faster than staging + committing in the shell |
| 10 | **The proof beat** | `CommitGraphCanvas` re-render after the commit | The new commit appears at the branch tip and the current-branch accent moves to it — instantly, with no `.git/index.lock` prompt, no terminal, no "another process is using the repository" | No copy — the *absence* of a footgun is the message (**V-6** — Mainguard never claims what it didn't do) | Graph re-renders composed; the accent is already on the new tip (**M-2**) | **This is the "zero footguns" moment (§3)** |
| 11 | **A glimpse of craft** | `File → Theme` via `ThemeManager.ThemeChanged`; e.g. Midnight Loom → Daylight Loom | User switches theme; every token re-resolves live — the same shapes, spacing, and type, recolored. Daylight Loom proves the system isn't "a dark app" | Menu items are the theme proper nouns, Title Case (`Daylight Loom`) (**N-1**, **N-6**) | An instantaneous, calm cross-fade of color values only — no wipe, no flash, no assumed luminance direction (**M-4**, **M-2**) | The five-theme system is real craft, not a skin |
| 12 | **Keyboard-first, on demand** | `CommandPaletteView` overlay (T-18), opened with `Ctrl+P` from the default `ShortcutMap` | *Only if the user reaches for it:* the palette fades in over the scrim with ranked, highlighted actions and gesture chips — the discoverability surface that replaces a forced tour | Palette placeholder `Search actions, branches, repositories` · gesture chips render the real bindings (**V-1**, **N-6**) | Overlay fades in over the full-bleed scrim (`#C0000000`) with the one allowed soft `BoxShadow`; card does not scale-pop (**M-3**) | Everything is reachable without a manual — the engineer finds it when they want it |

Steps 1–5 are *setup and should feel like none*. Step 6 is the emotional peak. Steps 7–10 convert the peak into the product's actual promise. Steps 11–12 reveal craft and depth **only as the user reaches for them** — never pushed.

---

## 2. Empty → populated transition (brand-new user)

A first-run user has an empty workspace, and Mainguard treats emptiness as a *calm fact with one next step*, never an apology or a blank void. The transition is a chain of `ES`-pattern states resolving into content — owned by the EmptyStates doc, sequenced here.

| Moment | State | Owning surface | Resolves when | Rule |
|---|---|---|---|---|
| Before any repo | `No repository open` empty state | `CloneDashboardView` / `MainWindow` first run | A repo is opened or cloned (steps 1–5) | **ES-1**, **ES-2** |
| Repo open, nothing selected | Graph populated; detail pane invites a selection | `RepoDashboardView` → `CommitTimelineView` | The user selects a commit (step 7) | **ES-1** (calm fact, no action forced) |
| Staging panel, clean tree | Quiet affirmation, not a void | `StagingPanelView` | The user makes an edit → the watcher surfaces it (step 8) | **ES-4** (delight reserved for genuine, healthy emptiness) |
| Feature panels not connected | `Pull requests need a connected host` (and siblings) | `PullRequestsWindow` / `IssuesWindow` / `NotificationsWindow` | The user signs in to a host (deferred, never blocking first run) | **ES-3** (a not-connected state is an *empty state, not an error* — no `DangerBrush`) |

**The governing rule for the transition:** a first-run user must never hit a `DangerBrush` or an alarm simply for being new. A brand-new repo, a clean tree, and an unconnected host are all *empty states* (**ES-3**, **ES-4**, **V-2**) — the only red a new user should ever see is one they earned by doing something genuinely destructive.

---

## 3. The 60-second aha — thesis

**The single moment that proves the product** is **step 10**: the first commit lands and the graph re-paints at the branch tip *instantly, with no `.git/index.lock` prompt and no dropped-to-terminal recovery.* That is PRODUCT.md's success definition made physical — *"faster and less error-prone than the CLI or existing GUIs, with zero `.git/index.lock`-style footguns."*

**Why this moment and not a flashier one.** The 60fps graph render (step 6) is the *hook* — it's what a CLI and a web-wrapped GUI visibly cannot do, and it earns the first breath of trust. But the graph is aesthetic proof; the *product* proof is the commit that lands cleanly. The aha is deliberately an **absence**: the footgun that doesn't fire. Mainguard exists to prevent exactly the `.git/index.lock` collision class of bug (AGENTS.md; the reason every LibGit2Sharp handle is opened and disposed deterministically through `IGitService.ExecuteWithRepo`). A new user won't consciously notice the lock that never appeared — but they will feel that the tool never once made them think about the plumbing. That is the whole promise.

**How the first run drives to it, inside 60 seconds:**

1. **~0–20s — get to real content.** One accent action → clone with monotonic, honest progress (**M-6**) → workspace. No account wall, no tour, no settings gauntlet. Setup that feels like none.
2. **~20–35s — the hook.** The `CommitGraphCanvas` paints the full history composed at 60fps (**M-2**). The user's first involuntary reaction ("this is *fast*") happens here.
3. **~35–55s — the proof.** Stage a hunk, commit with `Ctrl+Enter`, watch the pre-commit scan clear and the working tree go clean (**ES-4**). Two keystrokes did what the shell needs several commands for.
4. **~55–60s — the payoff.** The graph re-renders with the new commit at the tip and the accent already on it — no lock, no terminal, no ceremony (step 10). *That* is the aha.

If a first-time user reaches step 10 and thinks *"that was faster than the terminal and it never got in my way,"* the first run succeeded. Everything else — themes, palette, the feature panels — is depth they'll discover on their own schedule.

---

## 4. Principles for NOT over-onboarding

Mainguard is a precision instrument for a serious engineer's high-focus, high-stakes work (PRODUCT.md, Users). Onboarding must respect that time. Five principles, each tied to the system:

1. **No forced tour, ever.** There is no coach-mark overlay, no multi-step wizard, no "Next →" gauntlet, no dismissible tips scrim on first launch. The only first-run surface is the single `No repository open` empty state (**ES-1**/**ES-2**). The product teaches itself by being legible.

2. **One next step at a time.** Each state offers exactly one emphasized action through the single allowed `Button.Accent` (**ES-2**, DESIGN.md One-Accent Rule). A new user is never asked to choose among five things or configure anything before they can work. Open or clone — that's the whole decision.

3. **Progressive disclosure — depth on demand, not on arrival.** Themes (`File → Theme`), the command palette (`Ctrl+P`, T-18), signing, host connections, and the feature panels are *reachable* from the first second but *pushed* at no point. The palette **is** the discoverability model: everything is one keystroke away for the user who reaches, invisible to the user who doesn't.

4. **Keyboard-first respect.** The default `ShortcutMap` (Ctrl+P palette, Ctrl+Enter commit, Ctrl+Shift+P push, F5 refresh, Ctrl+B new branch) means a proficient user can drive the entire first run — clone, stage, commit — without a mouse-led tutorial. Onboarding never traps focus in a modal the keyboard can't dismiss.

5. **Motion that informs, never entertains.** No confetti, no celebratory sweep, no bounce, no animated walkthrough (**M-2**, **M-5**). The one earned delight in the whole first run is a single ~140ms confirmation-pill fade on clone completion (**M-1**) and the quiet clean-tree affirmation (**ES-4**). A reduced-motion preference collapses even those to instant state changes with meaning intact (**M-7**). Delight here is earned by restraint — the mark of a premium instrument, not a consumer app.

**The anti-reference to hold the line against:** the "VS Code extension" / consumer-app onboarding — the modal welcome tour, the gamified checklist, the mascot. None of that fits a tool whose users are auditing it for trust (PRODUCT.md Anti-references, **V-3**). If a proposed first-run flourish would read as a hobby toy, it's off-system.

---

## 5. [Horizon] — agent onboarding (non-actionable)

*This section is a stance record, not a spec. The multi-agent control center is on the `phase2` roadmap and is not built; nothing here authorizes building first-run UI for it (Design Principle 5; DESIGN.md §6).*

When the swarm features land, the first time a user brings up an agent should inherit **exactly** the restraint above: no wizard, one honest empty state (`No agents running` in the `ES-1` shape), and delight reserved for the genuine emotional peak — an agent's work passing verification (**M-1** fade, **T-4** toast, never a fanfare). Agent identity in any future onboarding stays a neutral working name paired with verifiable status (**N-4**, **N-3**, **V-6**) — `Loom-1 passed verification — 214 tests green`, never a mascot or a mood word. The single-user aha (§3) is the template: prove the promise with an *absence of footgun* (no clobbered working directory), not a tour. Concrete first-run agent UI is out of scope until that work is designed.

---

## Appendix — Self-gate

| Gate | Result |
|---|---|
| Storyboard names only real views/controls | Pass — `MainWindow`, `CloneDashboardView`/`CloneDashboardViewModel` (T-21), `ICloneService`/`CloneProgress`, `RepoDashboardView`, `CommitGraphCanvas`, `StagingPanelView`, `DiffViewerView`, `CommitTimelineView`/`CommitRowViewModel`, `CommitComposerView` (T-31), `PreCommitFindingsView` (T-30), `CommandPaletteView` + `ShortcutMap` (T-18), `ThemeManager` — all from the `docs/repo-map/` index |
| Every step cites Bible rules | Pass — V-1/V-2/V-3/V-4/V-5/V-6, ES-1/ES-2/ES-3/ES-4, T-1, TT-1/TT-2/TT-3, M-1/M-2/M-3/M-4/M-6/M-7, N-1/N-4/N-6 |
| Motion restrained (120–150ms, no bounce, no layout shift) | Pass — only earned motion is one ~140ms clone pill (M-1) and calm fades; graph render and theme switch are explicitly silent (M-2/M-4); reduced motion honored (M-7) |
| Aha ties to PRODUCT.md success definition | Pass — §3 anchors the aha to *"faster and less error-prone than the CLI… zero `.git/index.lock`-style footguns"*; the proof beat is the lock that never fires |
| Not over-onboarding | Pass — §4: no forced tour, one accent action per state, progressive disclosure, keyboard-first (default `ShortcutMap`), motion informs not entertains |
| No raw colors | Pass — only named tokens/roles referenced (`Button.Accent`/`Button.Secondary`, `AccentBrush`, `AccentSelection`, `DangerBrush`, scrim `#C0000000` cited as the system's literal-per-overlay exception per DESIGN.md §4) |
| Empty→populated ties to EmptyStates, no duplication | Pass — §2 sequences the `ES`-pattern states and defers their copy/art to `docs/creative/EmptyStates.md` |
| Agent/swarm content deferred | Pass — §5 marked **[Horizon]**, non-actionable, stance-only |
| No blank table cells | Pass — every cell filled |

**Rules cited:** V-1, V-2, V-3, V-4, V-5, V-6 · ES-1, ES-2, ES-3, ES-4 · T-1 · TT-1, TT-2, TT-3 · M-1, M-2, M-3, M-4, M-5, M-6, M-7 · N-1, N-3, N-4, N-6 · DESIGN.md One-Accent Rule, §4 Elevation (scrim), §5 Selection.

**Deferred:** concrete agent/swarm first-run UI (§5, [Horizon]); EmptyStates copy/art (owned by `docs/creative/EmptyStates.md`); the marketing-register onboarding of the site (separate `brand` pass).
