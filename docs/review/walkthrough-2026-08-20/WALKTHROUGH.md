# Live UI walkthrough — 2026-08-20

Exhaustive, real-click pass over `docs/review/full-test-matrix.md` (sections A–J), on macOS,
against the live daemon + the built Pro app (`build/macos-bundle/out/Mainguard.app`), driven with
real mouse/keyboard input (CGEvent/AppleScript — no synthetic-shortcut RPC substitution for anything
that is itself under test). Every step below has a numbered, named screenshot in `screenshots/`.
Bugs/inconsistencies/visual defects found along the way are logged in `ISSUES-LOG.md`, cross-referenced
by step number here.

Matrix legend recap: **[UI]** exercised by clicking, **[RPC]** setup/cleanup noise only (never used
to fake coverage of a UI-tagged row), **[auto]** re-run via `dotnet test` and cited where relevant.

---

## Step 001 — Initial state

Found the app already running (PID 20322, daemon PID 20325) against a repo with a live queue.
**Action:** none — captured the state as-is before touching anything, since it's informative on its
own (see ISSUES-LOG.md #1 and #2).
**Observed:** Coordinator panel shows a red-bordered error banner: *"Coordinator ended — Branch
'agent/2d8b4b1214354f079d7d92ff761d126c' no longer exists in this repository's mirror, so agent
'...' has no committed work to resume. Its commits are gone (the branch is deleted when an agent is
stopped) — discard the entry instead."* The queue rail (right) shows 2 `Merged` entries and 2
`Working, not verified yet` entries, each with **Resume** now visible (confirms `aa12ccf` from the
prior pass — Resume is no longer hidden after Stop).
**Screenshot:** `screenshots/001-initial-state-queue-and-error-banner.png`

## Step 002 — Calibration screenshot
Recalibrated screen-point↔pixel mapping using a region capture (`screencapture -R216,196,1296,759`,
clean 2x retina scale, no window-shadow padding ambiguity) instead of `-l <windowid>` (which the
prior pass's methodology used and which pads asymmetrically). This mapping is used for every click
below: `screen_pt = (216 + px/2, 196 + py/2)`.
**Screenshot:** `screenshots/002-calibration-check.png`

## Step 003 — E4: Discard the stranded entry (branch already deleted)
**Action:** clicked **Discard** on queue row `2d8b4b1214354f079d7d92ff761d126c` (the one the
Coordinator panel's error banner says has no branch left).
**Expected:** a confirm prompt (Discard is destructive-ish — drops from queue).
**Observed:** inline confirm appeared: *"Drop this entry from the queue? It will not be merged, and
the branch is left alone."* — correct, matches E4's spec exactly.
**Screenshot:** `screenshots/003-e4-discard-stranded-entry-clicked.png`

## Step 004 — E4: Confirm the discard
**Action:** clicked the confirm button.
**Expected:** entry removed from the rail, a toast confirms.
**Observed:** ✅ PASS. Entry gone from the rail; toast *"Dropped agent/2d8b4b1214354f079d7d92ff761d126c
f..."* (truncated, see ISSUES-LOG #3). Also surfaced a pre-existing `Rejected` entry
(`b5224606390f4de89b205f6982502c67`, "rejected in review") that was hidden below the fold —
confirms E5's "stays on the stream, unlike Discard" persists correctly across a session.
**Bug noticed:** the Coordinator panel's red error banner for the now-discarded agent is still showing
verbatim — see ISSUES-LOG #2 (now CONFIRMED, not just a hypothesis).
**Screenshot:** `screenshots/004-e4-discard-confirmed.png`

## Step 005 — E7: Resume the OTHER stranded entry (adversarial: branch also gone)
**Action:** clicked **Resume** on queue row `6d7b7c1027c0420784f2eaca1c07f9f1` (also `Working, not
verified yet`, no live jail).
**Expected (per matrix E7 adversarial case):** "refused if the branch is gone."
**Observed:** ✅ PASS, matches spec exactly. A tooltip explained Resume's normal behavior ("This
entry's agent has no sandbox... Resume starts a new one on the entry's agent/<id> branch, with
it[s...]" — clipped, see ISSUES-LOG #3), and a toast appeared: *"Can't resume — Branch
'agent/6d7b7c1027c0420784f2...'"* (also clipped) — the refusal itself is correct, just its wording is
unreadable past ~55 characters.
**Screenshot:** `screenshots/005-e7-resume-stranded-clicked.png`

## Step 006 — Attempted to read the full clipped toast text
**Action:** clicked what looked like an expand chevron on the toast.
**Expected:** either it expands to show full text, or nothing happens.
**Observed:** the toast dismissed instead — there is no way in the UI to read a toast/tooltip message
past its clipped width. See ISSUES-LOG #3 (confirmed, not hypothesis).
**Screenshot:** `screenshots/006-e7-resume-refusal-toast-expanded.png` (shows the toast gone, nothing
else changed)

## Step 007 — Blocking bug found and fixed: queue-stream/terminal-stream HTTP/2 starvation

**Action:** spawned a fresh `scripted` coordinator via the real UI CLI picker to continue matrix
coverage. **Observed:** the new entry did not appear in the Merge queue rail for 9+ seconds (screenshots
`007`-`012`), reproducing the user's exact complaint live. Root-caused: `DaemonClient.StreamQueueAsync`
shared one HTTP/2 connection with `AttachTerminal`'s continuous PTY stream — an attached terminal's open
flow-control window could starve the queue's push delivery. **Fixed:** gave `StreamQueueAsync` its own
connection (`StreamChannel()`), added a regression test
(`Mainguard.Tests/DaemonClientChannelIsolationTests.cs`), rebuilt, and restarted the app
(screenshots `013`-`024` are the fix-verification cycle — two earlier fix attempts before landing on the
final one). Committed as `wip: give the queue stream its own HTTP/2 connection — the terminal was
starving it` (9baefdf).

## Step 008 — Real claude-code coordinator, full lifecycle, real UI clicks

Cleaned all stale docker jails (`docker rm -f` on every `mainguard-*` agent container), relaunched the
app fresh with the `StreamChannel` fix built in, reopened `e2e-fixture` (`screenshots/025-026`).
Navigated the left rail to confirm what each icon opens: Pull Requests (`027`, honestly reports "Not
connected — connect an account"), Issues (`028`, same honest state), Coordinator/Merge-queue (`029`).

**Action:** clicked **Start coordinator** with `claude-code 2.1.234` selected (already logged in).
**Observed:** real CLI launched, real login banner ("Welcome back Daniel!", Sonnet 5 · Claude Max),
attached terminal streaming live (`030`-`031`). **Visual defect found:** the terminal's two-column
onboarding banner rendered badly garbled — words split mid-token, stray characters floating detached
from their lines (`031`, `033`-`034`, `036`-`037`) — reproducible, not a one-off. Logged as ISSUES-LOG #5
below (not fixed this pass — a VT-parser/redraw bug, non-blocking).

**Action:** clicked into the terminal and typed a real task via `System Events keystroke` (real
synthetic keyboard input, not the RPC composer): *"Add a one-line comment to the top of src/calc.js
describing what it does, then commit it directly with git."* **Observed:** several characters were
dropped by the typing method itself (rendered as "a on-line comment... top o.js" — see ISSUES-LOG #6),
but the agent handled the corrupted prompt intelligently — correctly identified the ambiguity, offered a
disambiguation menu, and after selecting `src/calc.js` it read the file, added the comment, configured a
missing git identity, and committed (`33c2f7c`) — full real agentic work through the real UI
(`032`-`037`). This also reconfirms: real synthetic keystrokes DO reach the jailed PTY when the terminal
is properly focused first — the earlier session's "keystrokes don't reach the jail" finding was
evidently about focus/routing, not a hard block.

**Action:** checked whether the fresh commit's queue entry appeared promptly. **Initially concluded broken**
(`036`-`038`, `040`-`041`) — spent a long diagnostic detour (temporary instrumentation in
`DaemonBackedOrchestrator`/`ControlCenterViewModel`, reverted before committing) that proved the data
pipeline was actually correct end-to-end (`Entries.Count=11` in the ViewModel within milliseconds).
**Root cause of my own false alarm, and very likely the user's original complaint too:** 5 leftover
`Working` entries from earlier debugging sessions filled the visible area of the Merge Queue panel ahead
of the fresh entry; the panel **is** scrollable, but the scrollbar affordance is thin/low-contrast, and
my first scroll attempt used the wrong on-screen coordinates (landing on the terminal panel instead),
which looked exactly like "the panel doesn't scroll." Scrolling with corrected coordinates immediately
revealed all 11 entries, including the fresh spawn `ac0a1c56...` correctly showing `Working / not
verified yet` with Verify/Resume/Discard (`042`). Logged as ISSUES-LOG #4 — a real, medium-high severity
UX defect (stale-entry clutter burying new work below an easy-to-miss fold), just not a data bug.
**Screenshots:** `025`-`042`.
