# Mainguard Voice & Delight Bible

**The durable anchor for how Mainguard speaks, moves, and names things.**

Creative North Star: **The Precision Loom** — a native instrument for high-stakes Git work, growing into a control center for supervising a swarm of coding agents. This document is the *contract* that every Wave-2 creative doc (empty states, microcopy library, motion spec, onboarding, launch copy) conforms to. Its rules are numbered and stable so downstream docs can cite them (e.g. "per **V-4**", "shape from **C-2**").

**Source-of-truth hierarchy.** [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) govern the design system, register, and brand personality; [`AGENTS.md`](../../AGENTS.md) is the Repository Map that pins every example below to a real view, control, token, or exception. Where this Bible and those files ever disagree, they win — and the drift gets fixed here. Nothing in this document invents a color, radius, spacing value, or motion outside the system.

**Scope of tense.** Rules stated in the present tense govern the shipped single-user Git client (the code today). Rules marked **[Horizon]** govern the multi-agent control center on the `phase2` roadmap; they specify *voice and naming that future work will inherit*, not UI to build now (per Design Principle 5 and DESIGN.md §6 "Don't design speculative UI"). A Wave-2 doc for a shipped surface should not cite a **[Horizon]** rule as license to build.

**How to cite.** Every rule has a stable ID: `V-#` voice, `E-#`/`C-#`/`T-#`/`TT-#`/`ES-#` microcopy patterns, `M-#` motion, `N-#` naming. IDs never get renumbered; retired rules are struck, not reused.

---

## 1. Voice Principles

Mainguard's brand personality (PRODUCT.md) is **premium & precise** — a high-craft instrument for serious engineering, *not* a hobby project, a themed web-view wrapper, or a bland enterprise dashboard. Voice is where that personality is heard.

**The register in one line:** Mainguard speaks like the instrument it is — it names the exact object, states the fact, and points to the way back, in the fewest words that do all three.

Eight principles, each a one-line rule with a do/don't drawn from a real surface. Every **don't** is a *near-miss* — the version a competent writer might actually ship — annotated with the specific way it fails. Strawmen teach nothing.

### V-1 — Precise over friendly
**Rule.** Name the exact object and the exact outcome. Never a vague reassurance where a concrete noun belongs.
Ties to *premium & precise*; rejects the hobby-project register.

- **Surface:** the `.git/index.lock` recovery notice raised when `GitServices.ExecuteWithRepo` finds a stale lock (the exact bug this app exists to prevent).
- **Do:** `A .git/index.lock file is left over in mainguard/. Another Git process usually leaves this behind when it exits early.`
- **Don't:** `The repository is locked by another process. Try again later.` — reads professional, but names no file and no path, and "try again later" is hope, not a way back.

### V-2 — Calm, never alarmist
**Rule.** No exclamation marks, no "Warning!", no red-alert theatrics. Severity is carried by the *component role* (`Button.Danger`, the `DangerBrush` blocker dot) and a plain statement of consequence — not by louder words.
Rejects the anti-reference of corporate scare-modals (PRODUCT.md).

- **Surface:** the pre-commit scanner blocker banner in `PreCommitFindingsView` (T-30).
- **Do:** `Blocker: an AWS secret key was found in src/config.ts:42. Committing would write it into history.`
- **Don't:** `Warning: a secret key was detected in your commit!` — "Warning:" and the exclamation add volume, not information; the blocker role already carries severity, and "in your commit" is vaguer than `src/config.ts:42`.

### V-3 — Engineered, not cute
**Rule.** No mascots, no jokes, no emoji, no "we"/"oops"/"whoops" in product copy. The register is a serious instrument reporting facts. (Emoji and playful copy are fine in external launch/marketing register — a separate `brand` pass — never in the app.)
Protects against the "VS Code extension" / whimsical anti-reference.

- **Surface:** the operation-history status chips in `OperationHistoryWindow` (T-19).
- **Do:** `Superseded` · `Undone` · `Not undoable`
- **Don't:** `Undo complete — you're all set!` — warmth in place of fact; `Undone` says everything in one word, and "you're all set" is a claim about the user's state that Mainguard can't make.

### V-4 — Destructive-safety forward
**Rule.** Before any irreversible action, state (a) what will change, (b) what is recoverable, and (c) offer the safer path first. This is the product's core promise (Design Principle 4).

- **Surface:** the force-push confirmation for `RepoDashboardViewModel`'s `PushForceWithLease` command (T-10) — Mainguard uses `--force-with-lease`, never a bare `--force`.
- **Do:** `Force-push main to origin? This replaces the remote branch with your local history. Force-with-lease refuses if anyone has pushed since your last fetch, so their work can't be silently overwritten.` — primary action `Button.Danger` "Force-push (with lease)", `Button.Secondary` "Cancel".
- **Don't:** `Push failed. Use --force?` with a single "OK" — real GUIs ship this. It skips (a), (b), and (c) in one line: no statement of what force does, no recoverability, and the destructive path is the *only* path offered.

### V-5 — Point to the way back
**Rule.** When something is blocked or fails, tell the user how to recover in the same breath — undo, reflog, stash, restore. Mainguard's promise is that you never lose work; the copy must make the exit visible.

- **Surface:** `UndoBlockedException` surfaced from `OperationJournal.Undo` when the working tree is dirty (T-19).
- **Do:** `Can't undo this yet — undoing would overwrite uncommitted changes in 3 files. Commit or stash them first, then undo.`
- **Don't:** `Undo failed (dirty working tree).` — accurate and terse, which makes it tempting. But it states the cause in jargon and stops: no count, no consequence, no next step. Terseness without the way back is not precision.

### V-6 — Honest about the machine
**Rule.** Say what Mainguard did and did not do; never claim certainty it lacks. State provenance for anything the tool didn't author. **[Horizon]** every agent action is attributable and auditable — worker output is always labeled by which agent produced it and whether it was verified (the audit-trail personality, README §6).

- **Surface (today):** the same `.git/index.lock` notice (V-1) — Mainguard must not imply it created the lock.
- **Do:** `Mainguard didn't create this lock, so it won't be removed automatically. Remove it only if no other Git process is running.`
- **Don't:** `Cleaning up lock…` — helpful-sounding, but it silently deletes a file another process may hold, and the copy claims an act of tidying Mainguard has no right to perform.
- **Do [Horizon]:** `Agent Loom-3 wrote this branch. 214 tests passed in its sandbox — not yet reviewed by you.`

### V-7 — Economy is respect
**Rule.** Every string is read mid-task, on borrowed attention. Cut any word that adds no fact and no path — if the sentence still works without it, it was decoration. "Successfully" is the canonical casualty: the past tense already reports success.

- **Surface:** the branch-push toast in `BranchBrowserViewModel` — the shipped code is the don't.
- **Do:** `Pushed feature to origin.`
- **Don't:** `Successfully pushed 'feature'.` — "Successfully" restates the tense, and the quotes add ink where `TextBlock.Mono` already sets the ref apart. Every filler word delays the fact.

### V-8 — Mechanics of the register
**Rule.** The small choices that keep every string sounding like one instrument:

- **Contractions in refusals** — `can't`, `won't`, `isn't` read calm; "cannot" reads like a legal notice.
- **Never "please", never "sorry"** — politeness filler dilutes an instruction, and an instrument doesn't apologize. `Commit or stash them first`, not `Please commit your changes`.
- **Only confirmation titles ask questions** (C-1); statements end with periods; nothing ends with `!` (V-2).
- **Second person stays implicit** — bare imperatives (`Open Accounts`), never "you should" or "we recommend" (there is no "we", V-3).
- **Sentence case everywhere** — labels, buttons, titles (`Open repository`, not `Open Repository`); proper nouns and Git terms keep their own casing (`index.lock`, GitHub, Daylight Loom, N-6).
- **Front-load the object** — the first sentence must survive alone, because toasts and OS notification centers truncate (E-5).

- **Surface:** the missing-identity notice (`GitIdentityMissingException`, commit composer).
- **Do:** `Every commit is stamped with a name and email, and this repository has none set. Add one in Git Profiles.`
- **Don't:** `Please configure your Git identity before continuing.` — "Please" softens without informing, and "configure your identity" names no field, no place, and no reason.

---

## 2. Microcopy Patterns

Reusable templates. Each gives the **shape** (structure + tone rules) then 2–3 real filled examples on named surfaces. Copy that follows a pattern should cite its rule IDs. All examples respect the token/component rules: destructive = `Button.Danger`, cancel = `Button.Secondary`, at most one `Button.Accent` per surface, no raw color.

### 2.1 Errors — pattern `E`

**Shape.** `[What happened, with the exact object] + [why / what it means] + [the recovery path].` One to three plain sentences. No error codes as the headline; no stack-trace leakage.

- **E-1** — Lead with the concrete object and file/ref, never a generic subject (V-1).
- **E-2** — State consequence in the user's terms, not the library's (`GitOperationException` internals never surface verbatim).
- **E-3** — Always include the way back (V-5). If truly unrecoverable, say so explicitly.
- **E-4** — **Never echo a secret, token, or credential in an error string.** This is a hard invariant already enforced in code (`PreCommitFinding.Message` is rule-name + `path:line` only; `GitHubApiClient.Redact` scrubs host text). Copy must not reintroduce it.
- **E-5** — The first sentence must survive alone (V-8). Panels get three sentences at most; the object and the fact come first, the explanation second — toasts and OS notification centers truncate, and a truncated error must still name what happened.

**Examples.**
1. *Auth required* (`AuthenticationRequiredException` carrying `Host`, routed to that host's sign-in): `Mainguard needs to sign in to github.com to push this branch. Open Accounts to connect, then try again.` (E-1, E-3)
2. *Remote not found* (`RemoteNotFoundException` from the `ResolveRemoteName` resolver): `No remote named "upstream" is configured for this repository. Add it in Remotes, or push to origin instead.` (E-1, E-3)
3. *Bad signing key* (`GetSignatureStatuses` / signing path, T-15): `Signing is on, but the key 3AB4… couldn't sign this commit. Check the key in Preferences → Commit signing, or turn signing off to commit unsigned.` (E-2, E-3, E-4 — the key id, not the private key)

### 2.2 Destructive-action confirmations — pattern `C`

**Shape.** `Title = the action as a question naming the object.` `Body = what changes + what's recoverable + the safer alternative.` `Primary = Button.Danger with a verb-first label; Secondary = "Cancel".` (V-4.)

- **C-1** — Title is a question with the concrete ref/object: `Discard changes in 4 files?`, not `Are you sure?`.
- **C-2** — Body names recoverability explicitly: what the reflog/undo/stash still holds afterward, or that this cannot be undone.
- **C-3** — The destructive button label is the verb, never "OK/Yes" (`Discard`, `Force-push`, `Hard-reset`, `Delete`). Route it through `Button.Danger`; route Cancel through `Button.Secondary`. No `Button.Accent` on a destructive dialog.
- **C-4** — Prefer offering the safer path *inside* the dialog (stash instead of discard; merge instead of rebase) rather than a bare block.
- **C-5** — Match the actual guarantee: if Mainguard journals the op for undo (T-19) or it's reachable in the reflog (T-20), say so — that reassurance is the product.

**Examples.**
1. *Graph hard-reset* (the `IConfirmationService`-gated reset in `CommitTimelineView`'s context menu, T-09): `Hard-reset main to a1b2c3d? Commits after this point leave your branch and your working tree is replaced to match. They stay recoverable from the reflog (Repo → Reflog) until Git garbage-collects them.` — `Button.Danger` "Hard-reset", `Button.Secondary` "Cancel". (C-1, C-2, C-5)
2. *Drag-to-rebase flyout* (the two-action flyout from `LabelDragGesture` on dropping one ref chip onto another, T-09b): two labeled choices, verb + source + preposition + target — `Rebase feature onto main` and `Merge feature into main` — so the user picks the operation by name, never an ambiguous "Apply". (C-3)
3. *Reflog restore* (`ReflogWindow` per-row Restore = a confirmed hard reset, T-20): `Restore main to this entry? This hard-resets the branch to 9f8e7d6 and replaces the working tree. The move is itself journaled, so you can undo it from Operation history.` (C-2, C-5)

### 2.3 Toasts — pattern `T`

**Shape.** A radius-999 pill with `OnAccent` text (DESIGN.md §5 Chips/Pills). One line. Confirms a completed action or offers a single reversible follow-up. Fades per the motion budget (M-3); never blocks.

- **T-1** — Confirm in past tense with the object: `Merged feature into main.`, not `Success!`.
- **T-2** — At most one action verb in a toast, and only if it's genuinely reversible (Undo/Dismiss). Toasts are not error surfaces — an error uses pattern `E` in a panel, never a toast that auto-dismisses.
- **T-3** — No color-coded panic. A toast is informational; failure that needs a decision is a dialog, not a toast (V-2).
- **T-4 [Horizon]** — Agent-status toasts name the agent and the fact, never a mood: `Loom-2 passed verification — 214 tests green.` The verdict word (`passed`/`failed`) carries meaning; the pill styling stays token-driven, not a raw green/red.

**Examples.**
1. *Profile delete, cancel-safe* (`ProfilesWindow` delete → the Undo/Dismiss toast over `ProfileService.Restore`, T-21): `Deleted profile "Work".` with `Undo` · `Dismiss`. (T-1, T-2)
2. *Merge complete* (a fast-forward or merge landing through `GitServices`): `Merged feature into main. 12 files changed.` (T-1)
3. *Verification passed* **[Horizon]** (Semantic Conflict Verification, README §2): `Loom-3 passed verification in its sandbox — ready for your review.` (T-4)

### 2.4 Tooltips — pattern `TT`

**Shape.** A short fragment (no terminal period on a single fragment) that explains state or a shortcut. Reveals *why*, not *what's obviously visible*. Uses `FontUi`; SHAs and refs inside a tooltip use `TextBlock.Mono`.

- **TT-1** — Explain the non-obvious: a status, a boundary condition, a why. Don't restate the visible label ("Push" → "Push").
- **TT-2** — State the exact condition for a disabled control, so the tooltip doubles as the fix (pairs with V-5).
- **TT-3** — For signed/verified state, be exact about the trust level — Mainguard must not overstate verification (V-6).
- **TT-4** — A tooltip is supplementary, never the sole carrier. Hover-only text is invisible to keyboard flow; any state the user *must* know also lives in a visible label, chip, or panel. (The copy analogue of M-7: meaning survives with zero hover.)

**Examples.**
1. *Signature badge* (`CommitRowViewModel` verified/untrusted/bad badge, T-15): `Verified — signed by daniel@… with a key in your keyring` · `Signature can't be verified — the signing key isn't in your keyring` · `Bad signature — the commit's contents don't match its signature`. (TT-3)
2. *Stale fetch label* (the "last fetched N min ago" label with >15-min dimming, `RepoDashboardViewModel` + `AutoFetchService`, T-10): `Last fetched 22 min ago — this branch's ahead/behind counts may be out of date`. (TT-1)
3. *Disabled Create-PR* (the create form disabled on a detached/unborn HEAD in `PullRequestsViewModel`, T-23): `Check out a branch first — a pull request needs a branch head, and HEAD is detached`. (TT-2)

### 2.5 Empty states — pattern `ES`

**Shape.** `Hero (24px, weight 600) headline stating the plain fact` + `one Body (12–13px, TextMuted) line on how to fill it` + `at most one primary action (Button.Accent)`. Empty-state art uses the 48–64px shared icons. This is the rare place the Hero type scale appears (DESIGN.md §3).

- **ES-1** — Headline states what's absent as a calm fact, not an apology or a joke: `No repository open`, not `Nothing here yet 😴`.
- **ES-2** — Exactly one primary next step, routed through the single allowed `Button.Accent`. Secondary paths are `Button.Secondary` or plain links.
- **ES-3** — A "not connected / unsupported" state is an *empty state, not an error* — no `DangerBrush`, no alarm. It explains the capability and how to enable it (V-2).
- **ES-4** — Reserve delight for genuine emptiness of a healthy repo (e.g. a clean working tree), where a quiet affirmation is warranted; never dress up a missing-connection state as celebratory.

**Examples.**
1. *No repo* (`CloneDashboardView` / `MainWindow` first run): Hero `No repository open` · Body `Open a folder that's a Git repo, or clone one from a remote.` · `Button.Accent` "Open repository". (ES-1, ES-2)
2. *Not enough history* (`AnalyticsView`, `HasCommitData` false, T-22): Hero `Not enough history to chart yet` · Body `Analytics appears once this repository has a few commits.` — no action button. (ES-1)
3. *Host not connected* (the unsupported/not-connected affordance shared by `PullRequestsWindow` / `IssuesWindow` / `NotificationsWindow`, T-23/24/27): Hero `Pull requests need a connected host` · Body `Sign in to GitHub to see and open PRs for this repository.` · `Button.Accent` "Open Accounts". (ES-3)

---

## 3. Motion & Delight Vocabulary

Motion in Mainguard exists to **clarify state**, not to decorate (Design Principle 3). The budget is fixed by DESIGN.md: **120–150ms, opacity/brush transitions only, no bounce, no layout-affecting animation.** The signature button hover is a global 130ms `BrushTransition`. Delight is earned by *restraint* — a precision instrument that moves only when movement means something.

### The loom metaphor, inside the budget
The weave is Mainguard's delight language: a thread drawn **taut and settling without overshoot** — a single directional motion that arrives and stops. There is no spring, no elastic return, no bounce (that would read as a toy, violating *premium & precise*). Where the site expresses this literally (`WeaveHero`, `ThreadSpine`, `SuccessWeave` weaving a verified ring), the desktop app expresses it as disciplined fades and the round-capped linework of the graph. The commit graph draws with `PenLineCap.Round` — the "thread" character lives in the *rendering*, not in animation.

### M-1 — Moments that earn delight (and how much)
- **Merge / rebase success** — a single ~140ms fade-in of a `T`-pattern confirmation pill (T-1). That's the whole celebration.
- **Clean working tree** reached — a quiet affirmation in the staging panel (ES-4), fade only.
- **Clone completion** (`CloneDashboardView`, T-21) — the monotonic progress bar resolving to 100%, then a confirmation pill. The delight is the *honesty* of monotonic progress, not a flourish.
- **[Horizon] Agent verification passing** — the state transition to "Verified" and its toast (T-4). This is the emotional peak of the control center; keep it a fade, not a fanfare.

### M-2 — Moments that must stay silent
- **Commit-graph render** (`CommitGraphCanvas`) — the graph is an instrument readout. No per-row entrance animation, no staggered draw-in; it appears composed. It is virtualized at 60fps; motion there is scroll, not choreography.
- **Theme switch** (`ThemeManager.ThemeChanged`) — tokens cross-fade via `DynamicResource`; there is **no** celebratory transition, no wipe, no flash. The five themes swap calmly (M-4).
- **Selection** — the `AccentSelection` fill and 3px `AccentBrush` rail appear with the standard fade; the rail's column is pre-reserved so **nothing shifts** (DESIGN.md §5). No slide, no grow.
- **Destructive confirmations** — a scrim-backed overlay fades in; the card does not bounce or scale-pop. Gravity, not play (V-2, V-4).
- **Diff rendering / intra-line emphasis** — appears; is not animated.

### M-3 — Transition budget (hard limits)
Opacity and brush only; **120–150ms**; easing is a plain ease/linear with **no overshoot**; **no property that triggers layout** (width/height/margin) is animated. Overlays fade over the full-bleed scrim (`#C0000000`) with the one allowed soft `BoxShadow`. Any motion outside these bounds is off-system.

### M-4 — Theme-switch is state, not spectacle
A theme change re-resolves tokens live. Because Daylight Loom is light and the others are dark, motion here must never assume a direction of luminance change or add a reveal — it is an instantaneous, calm cross-fade of color values only.

### M-5 — Do / Don't (restraint)
- **Do:** on a successful merge, fade in `Merged feature into main.` as a pill over 140ms and let it rest, then fade out. One motion, one meaning.
- **Don't:** bounce or scale-pop the pill in, fire confetti, animate each commit row as the graph loads, or add a celebratory sweep to the theme switch. Any of these reads as a hobby toy and violates *premium & precise* and the no-bounce budget.

### M-6 — Progress is honest or absent
Long operations (clone, fetch, **[Horizon]** agent runs, verification) show **monotonic, real** progress (`CloneProgress` is monotonic by contract) or a neutral indeterminate `PathIcon.spinning`. Never a fake progress bar, never a jumping percentage. Honesty is the delight (V-6).

### M-7 — Reduced motion is respected
Where a surface animates (notably the marketing site's vignettes, which already honor `useReducedMotion`), a reduced-motion preference collapses motion to an instant state change. Meaning must survive with zero animation — motion is never the *only* signal of a state change.

---

## 4. Naming Conventions

Names keep the system coherent as it grows. Mainguard's naming draws from one family — **the loom and the weave** (thread, lane, weave, loom) — expressed with *premium & precise* restraint (no cute anthropomorphism).

### N-1 — Theme naming: a mood of light in the workshop
The five themes are **Midnight Loom** (default, dark charcoal), **Daylight Loom** (light), **Command Deck**, **Atelier**, **Loom Aurora**. The logic: each name evokes *a quality of light or a place of craft*, not its own hex values.
- *Midnight / Daylight* name the **light** (dark surface / light surface).
- *Command Deck* names the **register** — the control-center desk.
- *Atelier* names the **workshop** — the craftsman's studio.
- *Loom Aurora* names **luminous color** on the loom.

**Rule.** A new theme is named for a mood of light or a place of craft — one or two words, Title Case, ideally carrying "Loom" or a clear craft/light connotation. **Never name a theme after its literal palette** ("Dark Blue", "Green Mode") — that is literal-not-semantic (DESIGN.md's core rule) and breaks the moment the palette is retuned.

### N-2 — Feature & surface naming: the loom family, by meaning
Commit-graph columns are **lanes** (`Lane1`–`Lane5`, deliberately decoupled from semantic status colors). The North Star is **the Precision Loom**. New feature names may draw on the loom/weave family (thread, weave, lane) **only where the metaphor clarifies** — never as decoration that obscures the function. When in doubt, the plain engineering noun wins over a strained metaphor (a "Reflog viewer" stays a reflog viewer, not a "Time Loom").

### N-3 — State naming: name what is true
States are named by fact, matching the existing status-chip vocabulary: `Applied` · `Undone` · `Superseded` · `Not undoable` (T-19). **[Horizon]** agent and pipeline states extend this literally — `Verifying`, `Verified`, `Blocked`, `Quarantined` (the quarantine remote, ESC) — never mood words ("Happy path", "All good"). A state name must read the same in a calm log and an audit trail (V-6).

### N-4 — Agent naming **[Horizon]**: precise, not a pet
Worker agents are identified by a stable, neutral working name tied to their thread of work (e.g. `Loom-1…Loom-n`, or by assigned branch/task), never a whimsical mascot name. This matches *premium & precise* and avoids the hobby-project anti-reference. An agent's display always pairs its name with its verifiable status (N-3, V-6).

### N-5 — New surfaces stay in-system by construction
Any new surface inherits the existing rails, so naming never drifts:
- **Colors** get a **semantic token by meaning** in every `Themes/*.axaml` (never a hue name, never a raw hex) — DESIGN.md §2.
- **Recurring visuals** get a **component class by role** in `App.axaml` (`Button.*`, `Border.Card`, `Pill`) — never an inline one-off.
- **Copy** cites the pattern it follows (§2) and the voice principle it honors (§1).

### N-6 — Terminology & typography of names
- Git operations are lowercase, hyphenated where compound: `force-push`, `hard-reset`, `fast-forward`, `cherry-pick`, `index.lock`.
- Branch names, refs, and file paths render in `TextBlock.Mono`; SHAs are shortened to 7 hex chars in mono (`a1b2c3d`).
- Product/theme/feature proper nouns are Title Case (`Daylight Loom`, `Operation history`, `Reflog`).
- One term per concept across all surfaces: it's a **worktree** everywhere, never also "work tree" or "checkout copy"; **remote**, never also "server".

---

## Appendix A — The five-question gate

Run every new user-facing string through these before it ships. A string that clears all five is on-voice by construction; a string that fails any one names its own fix.

1. **Point at the object.** Does the string name the exact file, ref, count, or SHA — or a vague subject? (V-1)
2. **Where's the way back?** Does recovery appear in the same breath — undo, reflog, stash, resolver, Accounts — or does the string strand the user? (V-5, E-3)
3. **Would it read the same in an audit log?** No mood, no warmth-as-fact, no claim Mainguard can't verify. (V-6, N-3)
4. **Delete a word.** Does the sentence still work? Then the word was decoration — delete it and ask again. (V-7)
5. **Does severity ride the role?** Strip the component styling away: is the wording itself calm? Alarm belongs to `Button.Danger` and the blocker dot, never to punctuation. (V-2)

---

## Appendix B — Citing this Bible

Wave-2 docs should reference rules by ID inline, e.g.:

> Empty-state headline uses the Hero scale and states the plain fact (**ES-1**); the single action is the one allowed `Button.Accent` (**ES-2**); tone is calm-not-alarmist (**V-2**).

IDs are stable. When a rule is retired, it is struck through in place and its ID is never reissued, so a citation can never silently point at a different rule.

---

## Appendix C — Agent naming: the sanctioned options

*Folded verbatim from `LaunchReserve.md` §4 (2026-07-12 consolidation; LaunchReserve is archived at `docs/archive/LaunchReserve.md`). This appendix is the authoritative home of the agent-naming option table that extends N-4.*


Per **N-4** (agents are precise, not pets — a stable neutral working name tied to a thread of work,
never a whimsical mascot) and **N-2** (draw on the loom / weave family *only where the metaphor
clarifies*; the plain engineering noun wins when the metaphor strains). An agent's display **always**
pairs its name with its verifiable status (N-3, V-6): a name alone never implies trust.

**All names below are for [Horizon] features — the multi-agent layer is not built.**

| Option | Form | Rationale | Fit vs N-4 |
|---|---|---|---|
| **Loom-1 … Loom-N** *(recommended)* | `Loom-3` | Already the Bible's worked example (N-4, V-6, T-4). Ties directly to the North Star ("the Precision Loom"); the loom is the machine, each agent a numbered station on it. Neutral, sortable, audit-legible. | Strongest — stable, neutral, thread-of-work. |
| **Shuttle-1 … Shuttle-N** | `Shuttle-2` | The shuttle is the tool that carries a thread across the loom — a precise metaphor for a worker carrying one task across the repo. Evocative without anthropomorphizing. | Strong — clarifies (a shuttle *does* the carrying), stays a tool not a character. |
| **Thread-1 … Thread-N** | `Thread-4` | Names the *unit of work* the agent owns (a thread of the weave). Reads naturally in a log: "Thread-4 verified." Mild collision with OS "thread" in an engineering context. | Good — plain, but the word is overloaded. |
| **Heddle / Warp / Weft** (roles, not IDs) | `Warp`, `Weft` | Reserve weave-part nouns for *role* distinctions (e.g. a coordinator vs workers) if a role split ever ships — not for per-agent IDs. | Conditional — only if a role taxonomy is real; otherwise strained (N-2). |

**Recommendation.** Use **Loom-N** as the primary identifier (matches the Bible verbatim, lowest
risk), with **Shuttle-N** as the sanctioned alternative if a warmer-but-still-precise label is
wanted for marketing screenshots. Agents may alternatively be named by their assigned branch or task
(N-4) where that is clearer than an index.

**Anti-patterns (do not use):** mascot or pet names ("Sparky", "Buddy"), mood words as identifiers
("Happy-path", "Ninja"), or any name that implies a verdict the agent hasn't earned ("Trusty-1").
These violate N-4 and the hobby-project anti-reference (V-3). The verdict word lives in the *status*,
never the *name* (N-3).


---

## Appendix D — Release-notes voice guide

*Folded verbatim from `LaunchReserve.md` §6 (2026-07-12 consolidation). This appendix is the authoritative release-notes register — the instrument voice, one register warmer.*


How Mainguard announces changes. Terse, honest, user-benefit-first — the same instrument voice, one
register warmer.

**Rules.**

1. **Lead with the user benefit, name the exact object** (V-1). "Line-level staging now matches
   `git apply` exactly," not "Improved staging engine."
2. **Terse. One or two sentences per entry.** No changelog padding, no "we're excited to."
3. **Calm, no theatrics** (V-2). No exclamation marks, no "HUGE update." Severity and importance are
   carried by *what changed*, not by louder words.
4. **Honest about scope** (V-6). Say what changed and what didn't. If something is a fix, call it a
   fix; if a feature is partial or behind a flag, say so. Never announce a **[Horizon]** item as
   shipped.
5. **Group by what the user does**, not by internal module: *Staging*, *Conflicts*, *Graph*,
   *Themes* — not `PatchBuilder`, `CommitGraphRouter`.
6. **Git terms stay lowercase and hyphenated** (N-6): `force-push`, `hard-reset`, `fast-forward`,
   `cherry-pick`, `index.lock`.
7. **No emoji in release-note bodies.** (Permissible in marketing register per V-3, but the changelog
   is close to the instrument — keep it clean.)

**Structure per release.** A one-line summary, then **Added / Changed / Fixed** groups. Each entry
is benefit-first.

### Example entry 1 — a feature

> ## 0.6 — Line-level staging
>
> Stage exactly the lines you mean, and trust that Git agrees.
>
> **Added**
> - **Drag-select individual lines to stage in the unified diff.** Previously staging stopped at the
>   hunk; you can now compose a commit line by line. The result is validated against `git apply`, so
>   what you stage is what Git records.
>
> **Fixed**
> - A stale `.git/index.lock` left by a crashed external process is now detected and explained,
>   rather than surfacing as an opaque failure. Mainguard does not remove a lock it didn't create — it
>   tells you how to check whether it's safe to remove.

### Example entry 2 — a smaller release

> ## 0.5.1 — Theme and conflict fixes
>
> **Changed**
> - **Loom Aurora** contrast raised on muted metadata so timestamps and hints stay legible on its
>   lighter panels. No layout or shape changed — color values only.
>
> **Fixed**
> - The 3-pane conflict resolver now keeps per-side undo history when you switch files mid-resolve,
>   instead of resetting it. Accept/reject on the wrong side is recoverable again.

