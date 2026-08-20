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
