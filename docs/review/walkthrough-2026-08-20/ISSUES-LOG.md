# Issues log — live UI walkthrough, 2026-08-20

Every bug, inconsistency, visual defect, or "something's off" noticed during the walkthrough,
regardless of severity. Cross-referenced to `WALKTHROUGH.md` step numbers. Status: **OPEN** (logged,
not fixed — non-blocking), **FIXED** (blocking, fixed inline this pass, commit noted), or
**CONFIRMED-EXISTING** (already known from the prior pass's scratchpad findings, re-verified here).

---

### 1. [OPEN, cosmetic/UX] Coordinator error banner has no dismiss/discard affordance in place
- **Step:** 001
- The red error banner in the Coordinator panel explains the agent must be discarded, but offers no
  button to do that from the banner itself — the user has to go find the matching row in the queue
  rail on the right and click Discard there. Two separate panels for one action.
- Not blocking; UX polish.

### 2. [CONFIRMED, OPEN] Coordinator-ended error banner never clears once its entry is gone
- **Steps:** 001, 004
- After Discarding the queue entry the banner referred to (`agent/2d8b4b1214354f079d7d92ff761d126c`),
  the Coordinator panel still shows the exact same red "Coordinator ended... discard the entry
  instead" banner verbatim — even though that entry no longer exists. The advice it's giving ("discard
  the entry instead") is now stale/impossible since there's nothing left to discard. Needs a fresh
  coordinator spawn to see whether starting a new one clears it (untested in this pass yet, will
  revisit).
- Severity: low-medium — not a correctness bug (no wrong action can be taken), but confusing/stale UI
  state that persists indefinitely.

### 3. [CONFIRMED, OPEN] Toast and tooltip text is clipped with no way to read the full message
- **Steps:** 004, 005, 006
- Multiple toasts/tooltips (the discard-success toast, the resume-refused toast, the Resume button's
  explanatory tooltip) all get cut off at a fixed width with `...` and no truncation-safe affordance —
  no hover-to-expand, no click-to-expand (clicking the chevron just dismisses it), no wrap. For
  anything with a branch/agent id embedded (which is most of these messages, since ids are 32-hex-char
  GUIDs), the actually-useful identifying part of the message is frequently the part that gets cut.
- Severity: medium — the refusal reasons are exactly the kind of message the matrix's own philosophy
  ("never a silent nothing, a typed refusal") depends on being legible; clipping them undermines that
  even though the daemon-side reason text is correct.

### 4. [OPEN, real UX defect — likely root cause of "spawned agent didn't appear in the queue"] Stale queue clutter pushes new entries below the fold with a near-invisible scroll affordance
- **Steps:** 025-042 (the fix-attempt interlude)
- **This is NOT a data bug.** Exhaustive diagnosis (temporary instrumentation in `DaemonBackedOrchestrator.QueuePumpAsync`/`ReconnectLoopAsync` and `ControlCenterViewModel.OnChanged`, reverted before commit) proved conclusively: the daemon sends the correct full entry list, `DaemonBackedOrchestrator._queue` receives it, `ApplyQueueUpdate` populates it, `Changed` fires, `QueueRailViewModel.Refresh()` runs, and `Entries.Count` is correct (11/11) — all within milliseconds of a real spawn. A freshly spawned coordinator (`ac0a1c56...`) landed correctly in the ViewModel's collection every time.
- **What actually happened:** the Merge Queue panel's `ItemsControl` (`QueueRailView.axaml`) IS wrapped in a working `ScrollViewer` — but 5 leftover `Working` entries from earlier debugging sessions (dead jails, `docker rm -f`'d hours ago, never Discarded through the UI) sat at the top of the list, ahead of the fresh spawn, filling the visible area. The scrollbar thumb is thin/low-contrast and easy to miss — I myself initially concluded the queue was "stuck" and spent a long diagnostic detour before realizing I simply hadn't scrolled down far enough (and my first scroll attempt used the wrong on-screen coordinates, landing on the terminal panel instead of the queue panel, which looked like confirmation the panel didn't scroll at all).
- **Why this matters:** this is very plausibly exactly what the user experienced ("the spawned agent didn't appear in the merge queue") — not a data/wiring failure, but a real discoverability problem: a handful of accumulated stale/dead entries can bury a brand-new, fully-live entry below the fold with no visual cue ("3 more below", a badge count, sorting live entries to the top) that anything is hidden.
- Severity: medium-high — no data is wrong or lost, but the practical, observed effect ("my agent isn't in the queue") is identical to a real bug from the user's perspective, and is easy to reproduce simply by letting a few dead entries accumulate (which happens naturally from normal testing/experimentation).
- Suggested fix direction (not implemented this pass — a design call, not a one-line fix): either sort live/actionable entries (`Working`/`Verified`/`AwaitingReview`) ahead of long-idle ones, add a small "N more" / unread-count affordance when the list overflows, or auto-collapse `Working` entries whose sandbox is confirmed gone (`HasLiveSandbox == false`) into a separate "stranded" sub-section instead of interleaving them with live rows.

### 5. [OPEN, real & reproducible] Coordinator terminal's onboarding banner renders badly garbled
- **Steps:** 008 (screenshots `031`, `033`, `034`, `036`, `037`)
- The claude-code CLI's two-column startup banner ("Welcome back Daniel!" / "Run /init...", "What's new" / changelog) renders with words split mid-token across lines, and stray disconnected characters/fragments floating away from their source lines (e.g. "n able ith arn ore:" scattered down the left margin). Reproduced across multiple screenshots over ~40 seconds, not a single-frame glitch.
- Not deeply root-caused (didn't chase into `VtScreen`/the ANSI parser this pass), but it is a real, user-visible rendering defect on a LIVE (not ghost) coordinator, distinct from the previously-known ghost-coordinator garbled-terminal finding — this is a second, independent data point that the terminal renderer has redraw/reflow bugs with claude-code's real multi-column TUI output, not just with stale/replayed scrollback.
- Severity: medium — cosmetic but genuinely impairs reading what the agent is doing during onboarding; did not block interaction (the agent still received input and worked correctly underneath the garbled display).

### 7. [OPEN, real bug — H3] "Coordinator (<cli>)" review title only works while the coordinator is still alive
- **Steps:** 048-049
- Opened Review on the `ac0a1c56...` queue entry (the real claude-code coordinator from step 008,
  which had since ended — "No coordinator running" showing in the Coordinator panel). The cockpit
  title read `Review — ac0a1c56b4b94d9b8e6a98c6dc625ef2 · agent/ac0a1c56... → main` — the raw GUID,
  not `Coordinator (claude-code)` as the earlier session's fix intended and as H3 explicitly asserts.
- **Root cause** (`Mainguard.Agents.UI/ViewModels/ControlCenterViewModel.cs:1122-1131`,
  `OpenReviewAsync`): the "Coordinator (name)" label is only synthesized by looking the agent up in
  `_agents.ListAgents()` (confirmed empty via a diagnostic RPC call — the coordinator process had
  already torn down, so it no longer appears in the live agent list at all). The label is never
  persisted onto the queue entry itself, only computed on-the-fly from the currently-live agent
  roster. Since **queue entries are long-lived by design** (E4/E5's whole point — Merged/Rejected
  entries stay on the stream permanently, confirmed in step 004), this means: the friendly coordinator
  label is only ever correct for the brief window the coordinator is still running, and permanently
  reverts to a bare GUID for the rest of that entry's (indefinite) life — which is the common case,
  since most coordinators finish and get torn down.
- Severity: low-medium — cosmetic, not a correctness/data bug (the GUID is still accurate and
  clickable), but it directly contradicts H3's tested assertion and defeats the point of the earlier
  fix for anyone reviewing history after the fact.
- Not fixed this pass (non-blocking): the real fix is a persisted field (store the role/name on the
  queue entry at spawn time, daemon-side, instead of a live-lookup join at review-open time) — a small
  design decision, not a one-line patch, so logged rather than rushed.

### 8. [OPEN, testing-methodology note] Tooltip/label clipping mid-word, no ellipsis, confirmed again
- **Step:** 048
- Hovering the `506a60e6...` row's id showed a tooltip that clipped mid-character with no `…` marker
  at all (`506a60e6e700471aa945fc`) — same family as ISSUES-LOG #3, just a second data point that it's
  not truncation-with-ellipsis, it's a hard pixel/width cutoff with nothing signaling more text exists.

### 6. [OPEN, testing-methodology note, not a Mainguard bug] `System Events keystroke` drops characters on fast/long strings
- **Step:** 008
- Typing a full sentence via `osascript -e 'tell application "System Events" to keystroke "..."'` into the terminal dropped several characters/words (rendered as "a on-line comment... top o.js" instead of "a one-line comment... top of src/calc.js"). This is very likely an AppleScript/Accessibility-API typing-speed limitation, not a Mainguard input-handling bug — the terminal reliably received and rendered whatever it was actually sent, and the agent's own excellent handling of the corrupted prompt (asking for disambiguation rather than guessing wrong) is itself a positive data point.
- Noted for the runbook: future live-agent legs should type in smaller chunks or with inter-keystroke delay, or route through `SendPromptAsync` (once its own known drop-on-cancel-race bug is fixed) rather than raw `System Events keystroke` for long strings.
