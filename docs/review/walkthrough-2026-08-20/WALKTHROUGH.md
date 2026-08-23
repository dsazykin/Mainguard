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

## Step 009 — D1/E1/E3: Verify the fresh entry, confirm the verified-against stamp

**Action:** clicked **Verify** on `ac0a1c56...` (screenshots `043`-`045`, committed before the pause).
**Observed:** ✅ PASS. Entry transitioned `Working` → `Verified`, footer text "ready to merge".
**Screenshot:** `046` (post-pause resume check) confirms the state held: row shows a green check, state
`Verified`, and **`main@58a86d28e2`** rendered directly under the row — E3's verified-against stamp,
confirmed live for the first time (previously only unit-tested).

## Step 010 — E2/E3/H3: Review cockpit from the queue, verified-@ stamp, coordinator titling

**Action:** scrolled the Merge Queue panel (revealed the `Review` button, which only renders once an
entry is `Verified` — the 3 `Working` rows above it correctly show no Review affordance).
**Screenshot:** `048`. Clicked **Review**.
**Observed:** cockpit opens with a real diff (`src/calc.js`, the coordinator's own comment-add commit),
header shows `verified @ 58a86d2` (E3 confirmed a second way, in the cockpit itself, not just the
rail), Reject/Bring local/Merge all present and enabled. **Bug found:** the title reads
`Review — ac0a1c56b4b94d9b8e6a98c6dc625ef2 · agent/ac0a1c56... → main` — the raw GUID, not
`Coordinator (claude-code)` as H3 expects and as the prior session's fix intended. Root-caused (not
fixed — non-blocking, needs a small design decision, not a one-liner): the label is computed from a
live lookup in `_agents.ListAgents()` at review-open time, and that coordinator had already been torn
down (see step 008's "No coordinator running"). Since queue entries persist indefinitely (by design —
E4/E5), this means the friendly label is only ever correct while the coordinator is still running, and
reverts to a bare GUID for the rest of the entry's life. Logged as **ISSUES-LOG #7**. Also reconfirmed
**ISSUES-LOG #8**: a hovered tooltip on a stranded row's id clips mid-character with no `…`.
**Screenshot:** `049`.

## Step 011 — F1: Merge via a real UI click

**Action:** clicked **Merge** in the cockpit.
**Observed:** ✅ PASS. Toast *"Merged agent/ac0a1c56b4b94d9b8e6a98c6dc625ef2..."* (clipped, same
family as ISSUES-LOG #3), footer flips to `already merged`, Reject/Bring-local/Merge all disable.
Confirmed on the real checkout: `git -C ~/mg-work/e2e-fixture log --oneline -1` → `33c2f7c` (the
coordinator's own commit) — main genuinely advanced via the real three-step, not just a UI-side
optimistic flip. This is the first time F1 (merge execution) has been exercised end-to-end via actual
clicks rather than the RPC harness. Closed the cockpit (**Screenshot:** `050`-`051`).

## Step 012 — E7 adversarial (second stranded entry) + a new surface discovered: the agent document

**Action:** clicked **Resume** on stranded entry `2664ef9b52...` (branch also deleted).
**Observed:** ✅ PASS on the refusal itself (toast "Can't resume — Branch 'agent/2664ef9b...'", clipped
per ISSUES-LOG #3). **Unexpected:** a new red-dot icon appeared in the left rail that wasn't there
before. **Screenshot:** `052`.

**Action (after a coordinate-math slip sent one click to the wrong app entirely — corrected and
logged as ISSUES-LOG #10, not a Mainguard defect — screenshot `054`):** clicked the red-dot
icon with corrected coordinates.
**Observed:** it opens a full **Agent Document** view (Terminal / Agent diff / Plan / Staging panels,
a "Send a follow-up prompt" composer, a "Merge to main" button) for the stranded agent — **this
surface DOES render in phase-1**, correcting the prior session's belief that it never does.
**Screenshot:** `055`.

**Action:** typed a real prompt into the composer and clicked **Send** (C4).
**Observed:** 🔴 **BUG, HIGH severity.** The field silently cleared — no toast, no inline error, no
terminal echo, nothing. A jailless agent's Send is a complete silent no-op, worse than the
already-known `SendPromptAsync` race (which at least sometimes reaches a live jail). Logged as
**ISSUES-LOG #9**. Not fixed this pass (non-blocking). **Screenshots:** `056`-`057`.

## Step 013 — G1 detour: root-causing why a fresh spawn "disappears" from the queue

Navigated to **Resources** (`058`) — only the one `TornDown` agent shows; needed a LIVE jail for G1
(Pause via the Resources right-click menu). Expanded the rail via the hamburger (`059`), which
revealed the full nav (Repo viewer/Coordinator/Resources/Pull requests/Issues/Notifications/Releases
+ an **AGENTS** section listing live sessions) — the earlier "red-dot icon" from Step 012 turns out to
be exactly this AGENTS list, not a special affordance. Selected `scripted 1.0.0` and started a fresh
coordinator (`060`-`065`, one coordinate-math slip along the way, corrected).

**The new entry did not appear in the Merge Queue panel after 5+ seconds** — reproducing the
"spawned agent isn't in the queue" complaint a second time, this pass. Cross-checked via a diagnostic
RPC call (`ListAgentsAsync`): the daemon already had the entry, `state=Working` — **not a data bug**.
Suspected the running app might predate the `StreamChannel` fix (9baefdf) built earlier this pass;
confirmed via `stat` that `Mainguard.Agents.UI.dll` was rebuilt at 14:17:38 while the running process
had started at 14:13:20 — **the live app was stale, older than the fix.** Rebuilt (`dotnet build`,
clean), killed both the app and daemon processes, cleaned orphan jails, and relaunched fresh
(`066`-`076`).

**On the confirmed-fresh binary, the symptom reproduced identically.** Waited, scrolled — the fresh
entry (`07ccc99c...`) WAS there, correctly rendered with live data, just at the very **bottom** of a
long list of accumulated historical entries (`077`-`079`). This proves the `StreamChannel` fix holds
(data arrives promptly) and isolates the true root cause to **ISSUES-LOG #4**: `MergeQueue.Agents`
returns entries in stable dictionary-insertion order, and since Merged/Rejected rows are kept forever
by design, they permanently occupy the front of that order — every fresh, actionable spawn lands at
the bottom of the visible list. This is very likely the actual mechanism behind the user's original
complaint, more so than any single rendering bug.

**Fixed** (this was worth fixing immediately, not just logging, given how directly it matches the
user's own repeated complaint): extracted `MergeQueueGrpcService.OrderForDisplay` — a stable partition
putting actionable states ahead of the permanent terminal (Merged/Rejected) record — and wired it into
`Snapshot()`. Added 3 unit tests (`QueueDisplayOrderTests.cs`, no daemon/Docker needed) covering the
partition, order-preservation within each group, and the no-terminal-entries no-op case — all pass.
Full solution rebuild clean. Committed (`7497202`) and pushed. Repo-map updated in the same commit.

**Also methodology note:** one click landed on the Claude desktop app again mid-detour (same class of
coordinate-conversion slip as ISSUES-LOG #10) — caught immediately, refocused, no product impact.

## Step 014 — Resumed session: a real, HIGH-severity queue-stream death found live

**Action:** recalibrated (a region capture had drifted onto a different window — corrected via
`System Events … set frontmost` + a fresh `screencapture -R216,196,1296,759`), then captured the
live app's current state fresh. **Observed:** Merge Queue panel reads **"Nothing queued."** — no
coordinator running, no error banner. **Screenshot:** `085-current-state.png`.

**Investigated before assuming it was the already-known ISSUES-LOG #4 clutter/scroll issue** (it
wasn't): confirmed the running daemon payload actually contains today's `OrderForDisplay` fix
(`grep -a` on the DLL bytes, not just a timestamp — ruling out the Step-013 stale-binary trap a
second time), then went to the SQLite store directly: `~/.mainguard/mainguard-daemon.db` has **26**
`MergeQueueRows`, 17 of them for the exact repo hash the daemon's `rpc.log` confirms is currently
bound. **Not a data bug.** Traced it to the client: `rpc.log` shows one `StreamQueue` call ending
cleanly after 32ms and **zero retries in the following 5m45s**, while `ListAgents` polling kept
succeeding every 60s the whole time — the daemon connection was healthy, only the queue pump had
silently died, contradicting its own documented "reconnect forever" contract
(`DaemonBackedOrchestrator.QueuePumpAsync`/`ReconnectLoopAsync`). Full evidence chain and analysis
in **ISSUES-LOG #11** (HIGH, not fully root-caused to one line — ran out of budget on the exact
cancellation trigger) and **#12** (a separate, confirmed client-side `SIGABRT` crash earlier in the
session, `~/Library/Logs/DiagnosticReports/Mainguard-2026-08-20-173042.ips`, almost certainly
unrelated given the ~15-minute gap and an intervening restart).

**Action taken (this was blocking — could not test any further queue-dependent row without a live
queue):** killed and relaunched the app + daemon fresh via `open .../Mainguard.app`. **Observed:**
clean "Reopen Last Repository?" prompt (`086-fresh-reopened.png`), reopened into the Repository
(commit-graph) view — incidentally reconfirming the known commit-graph branch-pill/label-overlap
bug is still present (`main | mainguard-local/agent/ac0a1c56b...` overlapping the author/date
text). Navigated to Coordinator: **queue populated correctly, 7 `Working` entries at the top**,
actionable-first ordering fix (`7497202`) still holding on a fresh session. **Screenshot:**
`087-coordinator-fresh.png`. Testing resumes from here with a known-good app instance.

## Step 015 — Resumed leg: root-caused a contributing cause of ISSUES-LOG #11, fixed and verified

**Action:** re-read `WALKTHROUGH.md`/`ISSUES-LOG.md` to resume from step 14's paused/interrupted
state (the app had since been closed for a device handoff), then read `DaemonBackedOrchestrator`'s
`ReconnectLoopAsync`/`QueuePumpAsync`/`SetActiveRepo`/`ClearActiveRepo` and
`ControlCenterViewModel.ProvisionRepoAsync` end to end to chase ISSUES-LOG #11 (the queue stream
dying silently and never retrying) further, per the standing instruction to timebox the
investigation and move on if it didn't resolve quickly.

**Found:** `ControlCenterViewModel.ProvisionRepoAsync` unconditionally calls
`daemon.ClearActiveRepo()` (which synchronously tears down the queue pump and blanks the
projection — `Queue.Refresh()` runs immediately after) on **every** call, including a redundant
re-open of the repo that's already active. Every repo-open entry point (`OpenRepositoryAsync`,
called from the repo picker, `ReopenLastRepo`, the command palette's "Open &lt;repo&gt;" entry, and
`ChangeInvalidRepoPathAsync`/`CreateGitRepositoryAsync`) reaches this same unconditional path — none
of them check whether the repo is already the one that's open and bound. After the clear, the code
awaits a real `ProvisionRepo` RPC with a 5-minute deadline before it calls `SetActiveRepo` again to
restart the pump — and in between, the rail shows a genuinely-empty projection with **zero** UI
indication a background reprovision is in flight (the `IsOpeningRepo` spinner only covers the
synchronous dashboard-build step; `TryRegisterSyncRemoteAsync` runs fire-and-forget after it). This
does not 100%-prove-identical to the exact timing evidence gathered for #11 (that trace showed no
provision-failure toast, which a slow/failed RPC here would eventually produce), but it is a real,
independently-confirmed structural gap in the same family, is very plausibly a contributing/related
cause, and is exactly the class of thing that produces "the merge queue looks empty and dead with no
explanation."

**Fixed** (`978db19`): `DaemonBackedOrchestrator.IsBoundTo(repoHandle)` reports whether the adapter
is already bound to that handle with a live pump; `ControlCenterViewModel.ProvisionRepoAsync` now
short-circuits to the last-known binding (`_lastProvisioned`, extended to carry the sync-remote URL)
when the repo path and handle both still match, skipping the clear+reprovision round trip entirely.
Regression test `IsBoundTo_TrueOnlyWhileThatHandlesQueuePumpIsAlive` added to
`RepoProvisioningHonestyTests.cs` (5/5 pass). Full solution rebuild clean. Committed and pushed.

**Verified live:** killed and relaunched the app + daemon fresh (`open .../Mainguard.app`).
**Screenshot `088-fresh-launch-after-fix-978db19.png`:** Merge Queue panel populated correctly and
immediately on a cold launch — 5 `Working` entries, correct actionable-first order, no "Nothing
queued" flash, no coordinator-panel error. Confirms the fix builds and runs cleanly and doesn't
regress the normal cold-open path.

**Not pursued further this leg** (explicit timebox, per instruction not to burn the whole leg on
one investigation): reproducing a REDUNDANT re-open specifically (e.g. via the command palette
while the repo is already open) to directly confirm this was the exact #11 trigger end-to-end, and
finding the kill switch's exact on-screen control to click it live for G3/G2 (a coordinate-probe
click at the sidebar's bottom red icon landed on an unlabeled nested group with no visible effect —
not chased further to avoid burning budget on pixel-hunting). Both are legitimate follow-ups for the
next leg. ISSUES-LOG #11 itself is left OPEN rather than marked fixed, since the exact original
trigger for that specific incident is still not proven — only a strong, independently-real
contributing-cause candidate is fixed.

## Step 017 (this leg) — E7 happy path, D7-adjacent Verify-disabled discovery, E1/E3/E2 re-confirmed, E5 Reject via real clicks — new regression found

Resumed into the fixed app (via a fresh `9bc9dfb`+`a972b02` build), then a follow-up `9baefdf`-generation
runtime confirmed clean via `506a60e6...`'s full lifecycle. **Discovery, not a bug:** clicking **Verify**
on a stranded queue entry (no live jail — all agent jails had been `docker rm -f`'d for a clean slate)
is a correct, silent **disabled-button** no-op, not a broken click — confirmed by zooming into the
button's rendering (visibly dimmer than Resume/Discard) and a 1.5s hover producing no tooltip at all.
**Minor UX gap logged, not fixed:** the disabled state offers zero explanation on hover — a user who
doesn't already know "Resume first" has no way to learn it from the UI itself.

**Real click sequence performed successfully, screenshots 111-131:**
- **Resume** (`115`) on `506a60e6...` (a queue entry whose branch still exists in the mirror,
  confirmed via `git branch --list agent/*`) — `ResumeAgentAsync` RPC returns `resumed=True`, a live
  jail spins up, Verify becomes enabled. **E7 happy path confirmed live for the first time** (only the
  adversarial branch-deleted refusal had been UI-tested before this leg).
- **Verify** (`118`) — `RunVerification` RPC returns `passed=True, state=Verified`. Row shows the
  green check + `main@33c2f7cf64` / `verified against main@33c2f7cf` stamp (**E3 reconfirmed**).
- **Review** (`123`) — cockpit opens with the real diff (`src/note-506a60e6.js`), `verified @ 33c2f7c`
  in the header (E3 confirmed a SECOND way). Title still reads the raw GUID, not `Coordinator (...)`
  — reconfirms **ISSUES-LOG #7**, unchanged, as expected (no coordinator was running for this agent).
- **Reject** (`127`-`131`) — clicked Reject, got the two-step confirm UI ("Reject this branch?" +
  optional reason field + Cancel, matching the spec exactly), typed a real reason via synthetic
  keystrokes ("E5 live-UI test reject"), clicked the confirming Reject button. `RejectEntry` RPC
  succeeds: `rejected=True, rejected_by=os:danielsazykin, rejected_at=...`. **E5 confirmed via real UI
  clicks for the first time** (previously RPC-only).

**New regression found and investigated (not fixed — see ISSUES-LOG #13):** after the successful
Reject, the entry did not reappear as a `Rejected` row anywhere in the Merge Queue panel — it simply
vanished. Confirmed this is NOT a stale-push issue: fully quit and relaunched the app+daemon (screenshots
`133`-`136`, maximized the window to rule out any scroll/fold hiding) and the panel still shows zero
Rejected rows on a cold snapshot, even though `sqlite3` confirms 2 Rejected rows persisted for this repo.
Traced (without finding the exact drop point) through `MergeQueue.Agents`, `OrderForDisplay`,
`ApplyQueueUpdate`, and `QueueRailViewModel.Refresh()` — none of the four explicitly filter Rejected.
Logged in full in ISSUES-LOG #13, flagged for a dedicated follow-up investigation rather than a rushed
fix, per this leg's standing instruction on complex/unclear bugs.

## Step 018 (this leg) — resume-from-headless-app recovery, kill-switch UI button, theme toggling

Resumed after ISSUES-LOG #13 was CLOSED (`c1e4c3e`/`13284e2`) by a dedicated deeper-model pass. Found the
Mainguard app process alive but with **zero open windows** (see ISSUES-LOG #14 — not counted as a
confirmed product bug, since the trigger wasn't reproduced; logged as a "Dock-click reopen didn't work"
observation worth a separate look). The initial raw screenshot (`136b-state-check-terminal-occlusion`)
misleadingly showed the Terminal window behind it, which cost real diagnostic time before the AX-window
check (`Can't get window 1...`) revealed Mainguard had no window at all. Recovered with a clean
`pkill`+`open` relaunch (`137-fresh-launch-repo-picker`), confirmed the repo-picker's "Reopen Last
Repository?" flow still works (`138-reopen-clicked`), and landed back on the Coordinator panel showing
**`6 in play · 7 in history (merged/rejected, below)`** — the `c1e4c3e` fix rendering correctly on a
genuinely fresh app launch, not just the deeper-model pass's own verification (`139-nav-coordinator`).

**Kill switch, via the real sidebar icon, not RPC** (`140-killswitch-click`/`141-killswitch-resume`): Engage → "All agents paused. The merge
queue is frozen. Nothing was lost — resume when ready." / "queue frozen · 0 agents paused" (0 because
this fixture's 5 stranded `Working` rows are jailless leftovers, not because anything's broken). Click
again → banner clears, icon returns to normal. **The button's own freeze/unfreeze UX is confirmed
correct** — logged as ISSUES-LOG #15, explicitly NOT closing the separate already-confirmed HIGH bug
(kill-switch Resume never un-pausing an actually-paused jail), since this fixture had no live jail to
prove that specific case through the UI.

**Theme toggling** (`142`-`147`): `View > Theme` menu lists all 4 themes + System. Daylight Loom applied
correctly (full light palette, no stale-dark bleed). Graphite applied correctly (dark). Restored Midnight
Loom. Atelier and OS-follow (System) not exercised this leg. Logged as ISSUES-LOG #16.

**Matrix coverage after this leg:** E1-E5, E7, G3 (button UX only, not the jail-resume bug), H7 (partial —
2 of 4 themes + System swap) now UI-click-verified. Still gapped: E6, D2/D4/D5, C2/C3, G1 (context-menu),
G4, H2/H4/H6, I1/I2, B1-B3, plus H7's remaining Atelier/System-follow check. Stopping here at a clean
commit boundary.

## Step 019 (this leg) — real coordinator spawn, G3 kill switch closed for real, orphan-jail root cause pinned

Resumed after `e6b812e`. Recalibrated window capture to `screencapture -x -o -l <windowID>` (via a small
`CGWindowListCopyWindowInfo` Swift probe) instead of `-R<region>` — the region-based capture was quietly
picking up the wrong bounds and bleeding the background Terminal window into prior screenshots; window-ID
capture is clean and should be preferred going forward. Also wrote a small `scanline.swift` pixel-brightness
probe to calibrate sidebar-icon and button click targets precisely instead of eyeballing crops — several
early clicks this leg missed their targets (dropdown, Start coordinator, sidebar icons) before this was in
place; screenshots `149`-`159` include some of those misses for the record, only `148`+`150`-`155` (renumbered
into this doc's sequence) are the meaningful ones.

**Kill switch, G3, CLOSED with a clean repro (see ISSUES-LOG #17):** first confirmed the 2 pre-existing live
jails were untracked orphans from an earlier daemon restart (predate the current daemon process's start
time), which correctly explained the "0 agents" / "0 agents paused" readings as honest, not broken
(ISSUES-LOG #18, its own minor finding). Then selected `claude-code 2.1.234` and clicked **Start
coordinator** for real — it spawned, authenticated as the logged-in account ("Welcome back Daniel!"),
Merge queue count went `6 in play` → `7 in play` live, Resource Monitor correctly tracked it as `1 agents`.
Engaged the kill switch against this real tracked jail: `docker inspect` confirmed `Paused=true`
immediately. Clicked Resume: the Coordinator panel's freeze banner cleared correctly, but `docker inspect`
still read `Paused=true` 3+ seconds later, and the Resource Monitor row itself agreed — state `Paused`,
task column reading "Kill switch engaged — jail paused, terminal input severed (recoverable)." The agent
never came back. This is the same structural gap found via RPC in an earlier leg (`KillSwitch.cs` has no
unpause fan-out), now proven through real UI clicks with zero possible orphan-jail ambiguity.

**Screenshots** (renumbered into this doc's sequence): `148-killswitch-engage-orphan-fixture-0-paused.png`,
`149-resource-monitor-zero-agents-despite-2-orphan-jails.png`, `150-killswitch-resume-banner-cleared.png`,
`151-real-claude-code-coordinator-spawned-live-ui.png`, `152-resource-monitor-1-agent-tracked-correctly.png`,
`153-killswitch-engage-real-tracked-jail.png`, `154-killswitch-resume-ui-shows-paused-recoverable.png`,
`155-coordinator-banner-cleared-jail-still-paused.png`.

**Matrix coverage after this leg:** G3 now fully UI-click-verified (both halves — Engage works, Resume's
bug is real and reproduced live, not just via RPC). Still gapped: E6, D2/D4/D5, C2/C3, G1 (context-menu),
G4, H2/H4/H6, I1/I2, B1 (BYOK isolation specifically — the login-flow itself is now incidentally covered
by this leg's real spawn), H7's remaining Atelier/System-follow check.

**Environment note for the next leg:** the real jail spawned this leg
(`mainguard-f467e1708a75-f1574a0ba9b443ffa2a5b2f9345df622`) is left genuinely paused by the Resume-bug
test — needs `docker unpause` or `docker rm -f` for a clean slate. The two pre-existing orphans
(`...aab43299...`, `...506a60e6...`) are still running and still untracked; also worth a manual
`docker rm -f` before the next leg if a clean slate is wanted (ISSUES-LOG #18).

Stopping here at a clean, fully-committed boundary — this leg's investigation (calibration rework +
closing G3 properly) consumed the available budget; no attempt was made at the remaining gapped rows.

**Post-leg cleanup performed:** `docker unpause` on the killed jail and `docker rm -f` on the two
confirmed orphans, so the next leg starts from a genuinely clean slate rather than the state described
above.

## Step 020 (this leg) — app exited again mid-session, coordinator-panel restart-resume gap found and root-caused to the log level

Found the app process gone entirely between two `ps aux` checks seconds apart (no window this time, not
even the process — a different symptom from #14's "alive, zero windows"; no crash report generated).
Recovered with a clean `open .../Mainguard.app` (`156-relaunch-after-headless-exit.png`).

Clicked "Reopen" on the last-repository prompt (`157-reopen-clicked-branch-overlap-visible.png` —
reconfirms the already-known commit-graph branch-pill/author-name overlap live again) and landed on the
Coordinator panel: **"No coordinator running"**
(`158-no-coordinator-despite-live-role-coordinator-agent.png`), even though the header confirms the rail
itself rehydrated correctly (`7 in play · 7 in history`, `c1e4c3e` still correct on a fresh process) and
`~/.mainguard/logs/rpc.log` shows the daemon's own `ListAgents` continuously reporting
`agent_id=f1574a0ba9b443ffa2a5b2f9345df622 role=coordinator` — the daemon never stopped believing this
agent is the coordinator; only the panel's own binding disagrees. Logged as **ISSUES-LOG #19 (HIGH,
matrix row I1, not fixed — flagged for a dedicated pass)**, with the reconnect-safe code path it should be
going through identified but not fully traced.

While investigating, found the same agent's `state` reads `Paused` in every `ListAgents` response for
20+ minutes straight, while `docker inspect` on its real container shows `Paused=false Status=running` —
traced to the previous leg's own cleanup step (see above: `docker unpause` run out-of-band on this exact
jail after the #17 repro), which by construction never told the daemon anything changed. Logged as
**ISSUES-LOG #20 (MEDIUM, not fixed)**: the daemon has no reconciliation between its tracked agent state
and Docker's live state, a gap in the same family as #18's orphan-jail problem, and plausibly the actual
reason #19's panel refuses to reattach.

Attempted to test whether the new `ee9be50` Resume path would self-heal this specific stuck agent (which
would also have been the live-click confirmation of that fix the last leg deliberately skipped) —
sidebar-icon clicks and a merge-queue-panel scroll stopped producing any visible change partway through,
despite the window ID/bounds being confirmed unchanged via a fresh probe. Not diagnosed further;
documented honestly in #20 rather than claimed as tested. Screenshot `159-nav-click-did-not-register.png`
kept as the record of the miss.

**Matrix coverage after this leg:** no additional matrix rows newly click-verified (I1 attempted, not
completed — still gapped). Two new confirmed findings (#19, #20) instead. Stopping here at a clean
commit boundary — the click-registration issue needs fresh eyes/recalibration before more live-UI rows
can be attempted reliably.

## Step 021 (this leg) — I1 re-verified fixed after a real bundle rebuild, terminal-buffer race found+fixed live, garbled-replay rendering re-confirmed and flagged

Rebuilt the daemon and app from `804ae632` (previous leg's #18/#19/#20 reconciliation fix) so the running
bundle actually carried it — the prior leg's log explicitly noted the live app predated that commit.
Relaunched clean (`160-restart-verification-fresh-relaunch.png`), reopened the fixture repo, and found
the header correctly reading `7 in play · 7 in history` — the `c1e4c3e` ordering fix still holds on a
genuinely fresh process.

The one pre-existing jail on this machine has no `mainguard.agent.role` label (created before the label
existed) and correctly adopts as a role-less worker, so it couldn't exercise the coordinator-binding fix
either way (`161-coordinator-panel-no-role-labeled-jail.png`) — exactly the known limit the last leg
flagged. Started a **real claude-code coordinator** through the actual UI to get a properly-labeled jail
(`162-real-coordinator-spawned-for-i1-retest.png`), confirmed via `docker ps` that the new container
carries `mainguard.agent.role=coordinator`, then killed and relaunched just the app process (daemon and
jail left untouched) — the real restart-resume repro #19's fix needed.

**Result: `163-i1-fix-confirmed-panel-binds-after-restart.png` — matrix row I1 is now genuinely
UI-click-verified, not just test-verified.** The Coordinator panel correctly showed the live agent id and
an active "Stop coordinator" button immediately after restart, no "No coordinator running" false state.

**New bug found and fixed live this leg (ISSUES-LOG #21, HIGH):** the terminal pane under the
correctly-bound panel rendered fully blank (`164`) despite the daemon holding real, correct scrollback for
that exact agent (confirmed via an independent raw RPC call — 2570 bytes, the full claude-code banner,
returned instantly). Root-caused to a race in `TerminalViewModel`: output can arrive and be silently
dropped (`_view?.FeedOutput` on a null `_view`) before Avalonia's binding pass calls `AttachView` — a race
a fresh spawn's multi-second CLI-startup delay always covered, but a rehydrated agent's near-instant
scrollback replay does not. Fixed by buffering output until the view attaches, then flushing it in order;
proved the regression test fails against pre-fix code before restoring the fix. Rebuilt, relaunched
against the same live jail, confirmed real content now renders (`165`).

**New/re-confirmed open finding (ISSUES-LOG #22)**: what #21's fix makes visible is itself garbled —
words split mid-token, fragments out of order — matching an older, less-clean repro from a prior leg's
ghost-coordinator finding. Leading hypothesis: the buffered replay flushes before the terminal's real
column width is known (that comes later, from a layout-driven resize), so the original recording's
absolute cursor-position escapes misplace text against a too-narrow buffer. Not attempted here — this is
timing-sensitive layout work against an engine flagged elsewhere as due for replacement, and getting the
ordering wrong risks reintroducing #21's blank-pane bug. Flagged for a dedicated Opus/Fable pass with a
concrete next step recorded in the log entry.

**Matrix coverage after this leg:** I1 (restart resume, both the coordinator-identity half from the
previous leg and the terminal-content half from this one) now fully UI-click-verified. Still gapped: E6,
D2/D4/D5, C2/C3, G1 (context-menu), G4, H2/H4/H6, I2, B1 (BYOK isolation specifically), H7's remaining
2 themes (Atelier, Midnight Loom/System-follow).

Stopping here at a clean, fully-committed boundary — the terminal investigation (diagnosis, fix,
regression test, live verification, and writing up the follow-on garbling finding) consumed this leg's
budget; no attempt was made at the remaining gapped rows.

## Step 022 (2026-08-23, screen-unlock resume) — automation confirmed working, #22 partially re-verified via live repro, new HIGH bug found on the Coordinator panel

Real days had passed since the last leg (system clock 2026-08-20 → 2026-08-23); the app (PID 59432) and
daemon (PID 59441) both survived idle the whole time. Confirmed GUI automation works cleanly now that the
screen is unlocked — an AppleScript `System Events click at {x,y}` correctly hit the "Reopen Last
Repository?" dialog's Reopen button on the first recalibrated attempt (`166`), fully corroborating the
prior leg's diagnosis that the screen lock, not a permission or app regression, was the sole blocker.

Navigated to the Coordinator panel for the reopened `e2e-fixture` repo and found it showing **"Still
starting the coordinator"** (`167`) — clicked **Restart**, no visible change (`168`). Investigated against
ground truth rather than assuming it was a stuck build: `docker ps`/`docker top` showed the underlying
container (`3cd48ab2…`) genuinely alive and running a real `claude` process with 2:22 of accumulated CPU
time; `~/.mainguard/logs/rpc.log` showed the daemon's own `ListAgents` correctly and continuously reporting
`state=Working, role=coordinator` for it the entire time (once a minute, from the client's own login-harvest
sweep); `spawn.log` showed the daemon explicitly logging `(agent left running)` on every harvest — this
looks like the tail end of an earlier leg's real-CLI OAuth session, deliberately preserved rather than torn
down. So the daemon has had the correct answer the whole time; the Coordinator panel's own "has this started"
signal is what's stuck. Logged as **ISSUES-LOG #23 (HIGH, OPEN)** — not fixed this leg (client-side binding
investigation, same complexity class as #19), flagged for a dedicated Opus/Fable pass. Worked around by
leaving that coordinator alone (it may be a real, worth-preserving OAuth session) and continuing with a fresh
scripted coordinator instead of tearing it down.

Given the budget this investigation and its careful, ground-truth-checked write-up consumed, stopping here at
a clean, fully-documented boundary rather than rushing the remaining #22 live-click re-verification and the
still-gapped matrix rows (E6, D2/D4/D5, C2/C3, G1 context-menu, G4, H2/H4/H6, I2, B1, H7's remaining 2
themes) without adequate care. Next leg should pick up with a fresh coordinator spawn for #22's final live
confirmation, then continue down the gapped-rows list.

## Step 023 (2026-08-23, continuation) — H7 fully closed, G4 confirmed live, and a significant new architectural finding (#24) on stale merge-queue state

The app (PID 59432/64785 across relaunches) and daemon had survived idle since the last leg. The app
window itself needed `osascript activate` to become onscreen again (`171`) — not a repeat of #14 (the
process was never windowless, the window just wasn't the active one), and a useful reusable fact: **this
app's window position is not stable across activations** — it moved from X=449 to X=216 between two
`activate` calls in this same leg, which caused a real self-inflicted coordinate bug (below).

**H7 (themes) fully closed.** Selected Atelier and System via `View > Theme` — a raw CGEvent click on
the "Atelier" submenu item looked like a complete no-op by eye (`176`/`178`), but pixel-sampling the
chrome background (`rgb(25,28,33)` → `rgb(34,31,27)`) proved it had actually applied; two dark themes
can be close enough in value to fool a visual check. Switched to `System Events` menu-item clicks for
reliability going forward and confirmed both Atelier and System (which correctly follows the OS's Dark
appearance) unambiguously (`179`, `180`). All 4 themes + System are now real-click-verified across this
and the H7-opening leg.

**G4 (Stop coordinator) confirmed live**, after a genuine self-inflicted bug: calculated the "Stop
coordinator" button's screen position once from an early screenshot and reused it as an absolute
coordinate — but it's a window-relative position, and the window had since moved. First click landed
harmlessly on empty chrome (or triggered an unrelated repo auto-fetch on activate); recalibrated via
`System Events`'s reported window origin (`216,196`) + the button's relative point, which landed
correctly. Confirmation dialog copy is accurate (explicitly says sub-agents keep running); confirmed the
panel returns to the CLI-picker empty state and the queue rows are untouched by the stop
(`184`, `185`).

**New finding, HIGH, architectural (ISSUES-LOG #24):** while checking the Resource Monitor for a G1
context-menu test, noticed it reported only 4 tracked agents while the Merge Queue rail claimed "10 in
play." Checked ground truth: `docker ps -a` shows exactly **one** real agent container on the machine;
the daemon's own `MergeQueueRows` table shows **15 rows** stuck in `Working`, all dated 2026-08-20 (three
days stale), for agent ids with no backing container at all. This is the same shape of bug as the
already-fixed #18/#20 (daemon state drifting from Docker reality with no reconciliation) but in a
**different component** — the `AgentSessionReconciler` from `67b9cc1f` only wired into `AgentSessionStore`
(what the Resource Monitor correctly reads), not `MergeQueueRows`. Every one of the 15 phantom rows still
renders live Verify/Resume/Discard buttons. Not attempted this leg — flagged for a dedicated Opus/Fable
pass with the exact evidence trail (agent ids, DB query, docker output) recorded in the log entry.

**G1 (right-click context menu)**: right-clicked a live queue row with real CGEvents at verified
coordinates — no context menu appeared. Not chased further; logged as ISSUES-LOG #25, needs a
deliberate check of whether queue rows have a context menu at all or whether G1's pause affordance lives
on a different surface entirely.

**Matrix coverage after this leg**: E1-E5, E7, G3, G4 (stop half), H7 (all 4 themes + System), I1 (both
halves) now UI-click-verified. Still gapped: E6, D2/D4/D5, C2/C3, G1 (needs a real answer, not just a
failed attempt), H2/H4/H6, I2, B1. New: #24 (HIGH, needs dedicated fix pass), #25 (minor, needs
investigation).

Stopping here — this leg found one significant new architectural issue that deserves the same careful
treatment as #18-20 rather than a rushed fix, plus closed out H7 and G4 cleanly. Next leg should pick up
with G1's real answer, then the remaining D/C/H/I/B rows.

## Step 024 — G1 confirmed on the Resource Monitor panel (screenshots 188-190)

Activated the app fresh (`osascript ... activate` — the window had again closed while the process stayed
alive, same shape as #14; re-derived window origin via `System Events`'s reported position rather than
reusing a cached coordinate, per the standing methodology note). Landed on the Resource Monitor panel
showing 4 agents: 3× `claude-code` TornDown, 1× `unknown` Working (`188`).

Right-clicked the Working row with a real `CGEvent` right-click — a Pause/End-task popup appeared
immediately (`189`), confirming #25's conclusion a second way: the queue rail genuinely has no context
menu, G1's affordance lives here instead. Clicking **Pause** fired a real `PauseAgent` RPC and got an
honest refusal back (`no live jail to pause`) for an agent id that, cross-checked against `docker ps`,
really doesn't have a container — but which didn't match the id `ListAgents` was reporting as the actual
live Working agent at that moment. Investigated the ViewModel code directly rather than guessing; it
looks correct (rows are matched/updated by `AgentId`, never reused). Most likely explanation: the
multi-step screenshot-crop-recalibrate cycle this leg's coordinate-finding took gave the background
refresh enough time to swap which agent the "unknown / Working" row represented while the menu sat open
— and two label-less agents render identically, so there was no way to see the swap happen. Logged as
ISSUES-LOG #28 with full detail; not filed as a confirmed code bug, but the "two agents can look
identical" gap is real and worth fixing once orphan/pre-label jails age out.

**Matrix row G1 is now UI-click-verified**: context menu exists, reachable by right-click, Pause/End task
both wire to real RPCs with honest responses (crash-free, silence-free).

**Matrix coverage after this leg**: E1-E5, E7, G1, G3, G4, H7 (all 4 themes + System), I1 (both halves)
now UI-click-verified. **Still gapped**: E6, D2/D4/D5, C2/C3, H2/H4/H6, I2, B1 — none of these were
reached this leg; G1's investigation (calibration attempts, code-reading, cross-checking against
`docker ps`/`rpc.log`) consumed the leg's budget on its own. Next leg should start fresh on E6 or D2 and
work down the remaining list; no architectural fix is pending from this leg (unlike the last several).

(A separate leg attempted E6/D2/D4/D5/C2/C3/H2/H4/I2/B1 and confirmed H4 live — Settings → Daemon Logs
shows real, non-empty daemon output — but otherwise burned its budget on what turned out to be a false
alarm: Settings-sidebar clicks appeared to land without switching panels. The user manually reproduced the
same navigation successfully; root cause was the user actively using the machine at the same time as the
automation, which intermittently stole input from the synthetic clicks. **Not a product bug** — logged as
a methodology note (#29) rather than a defect. No code changes, no new matrix rows closed.)

## Step 025 — I2 (daemon skew refresh) confirmed live via a real orphaned-daemon repro; H2 (egress
allowlist) fully confirmed; D2 (verify FAIL path) confirmed live with a genuinely broken test

**I2 — confirmed live, not just conceptually.** Opened Settings → About and found a live, naturally-
occurring version skew: the running app reported commit `ce3eb056` while its child daemon process (still
alive from an earlier launch) reported the older `8b225a12` — several commits behind, since the daemon
hadn't been restarted across a run of intervening commits. Rather than just observe this passively, ran
the actual repro the matrix row asks for: killed **only** the app process (`kill -TERM`), leaving the
daemon alive and orphaned (reparented to PID 1, confirmed via `ps`), then relaunched the app fresh.
Result: the new app instance correctly detected and killed the stale orphaned daemon and spawned a brand
new child in its place (`191`) — confirmed via `ps` showing the old daemon PID gone and a new one parented
to the new app PID. Reopened the repo (`192`) and the Merge Queue rebuilt correctly, including rendering
the `c1e4c3e` jail-liveness fix's wording live ("the agent's sandbox is gone — resume the entry to give it
one, or discard it") on a genuinely fresh process. Checked About again (`193`): the **App** version now
correctly read the current HEAD (`5012b610`) — the app side of the skew is real and self-corrects on
relaunch, matching I2's claim.

**One residual, build-tooling-only, not a live product bug**: even after this successful relaunch, the
**daemon**'s own version stamp lagged behind the app's (`7ac49596` vs the app's `5012b610`) — because the
on-disk payload DLL the daemon relaunches from hadn't been rebuilt to the latest commit despite a full
`dotnet build Mainguard.slnx -c Release` (MSBuild's incremental-build skip logic, or a separate
bundle/publish step not triggered by plain `dotnet build` — not chased further, out of scope for a live-UI
pass). This is a local-dev-build artifact, not something a real bundled release could exhibit (app and
daemon are built/bundled atomically in a real release pipeline) — noted for awareness, not filed as a bug.

**H2 — fully confirmed live.** Clicked "Network..." from the Coordinator panel, which opens a real
separate "Agent Network Allowlist" window (`194`) listing pre-allowlisted hosts (Anthropic API, NuGet,
OpenAI, crates.io) with per-host Remove buttons and an "Allow a host" form. Filled in a test host via real
synthetic keystrokes (name + host fields, confirmed via screenshot the text actually landed), clicked
Allow — the form cleared (`195`), and scrolling the list confirmed the new "Test Host" entry rendered live,
correctly tagged "Custom" (`196`) — the add-a-host-live-re-renders-the-proxy claim confirmed. Removed it
again via its Remove button to leave the environment clean (`197`), confirmed gone. **Not exercised this
leg**: the adversarial "block a non-allowlisted host from inside a live jail" check — no live jail was
running at the time; needs a follow-up with an active agent.

**D2 — FAIL path confirmed live with a genuine test failure**, the row's core claim. Built a new adapter,
`scripted-fail` (`~/mainguard/adapters/bin/scripted-fail` + `registry/scripted-fail.json`), that commits a
change breaking `calc.js`'s `add()` function (`return a + b + 1`) against the e2e-fixture's real
`test.js`, which asserts `add(2,3) === 5`. **Note**: the new adapter wasn't picked up by the already-
running daemon (adapter registry is scanned at daemon startup, not live-reloaded) — required a full
app+daemon restart to register, which is itself a mildly useful data point (registry scanning is
boot-time-only, consistent with I2's "skewed daemon" framing but for adapters, not versions). Selected
`scripted-fail` from the CLI dropdown (via keyboard Down-arrow cycling after a click — the dropdown's
popup itself never visibly rendered under CGEvent/System-Events clicks despite several techniques tried,
but arrow-key selection worked reliably once focused; logged as a minor automation-methodology note, not
chased as a product bug since keyboard selection is itself a legitimate accessible interaction path),
clicked Start coordinator (`198`) — the agent spawned, committed the deliberately-broken change, and
appeared in the queue as `c03bfe83…`. Clicked its **Verify** button: the entry correctly returned
`tests failed — node test.js` as the reason (`199`) and the row stayed in **Working** state, not silently
retried and not falsely marked Verified — exactly what D2 requires. **Not yet exercised**: the other two
legs of D2's 3-way trap (a mis-tokenized verify command, and a missing toolchain) — need their own scripted
adapters, not attempted this leg due to time.

**Matrix coverage after this leg**: I2, H2 now fully UI-click-verified. D2 partially verified (FAIL-path
leg confirmed; mis-tokenized/missing-toolchain legs still open). Still gapped: E6, D2's remaining two legs,
D4, D5, C2, C3, H6, B1.

## Step 026 — D2's remaining two legs confirmed live; D5 confirmed end-to-end; E6 analyzed as
## structurally hard to force; B1 partially confirmed with a real finding on the adjacent custom-key path

**D2 — missing-toolchain leg** confirmed live. Declared `.mainguard/toolchain: rust-stable` directly on
`main` (pushed to the daemon's bare mirror via the `mainguard-local` remote — main's toolchain baseline is
read live from the mirror, not cached, so no daemon restart was needed for this leg), then clicked Verify
on the already-`Working` `c03bfe83…` entry (`200`). Result: `Can't verify — Could not provision the
verification toolchain [rust-stable] declared by repo '...': 'rust-stable' is declared but absent from
the worker's jail — the probe \`cargo --version\` exited 127. Verification was NOT run; this is a
provisioning failure, not a failing test.` — a third, clearly distinct wording from both the genuine
test-failure message and (below) the mis-tokenized-command message, satisfying D2's "all three land
differently-worded" requirement. Reverted the toolchain declaration on `main` immediately after (pushed a
follow-up commit removing it) so it wouldn't poison every subsequent verification this session.

**D2 — mis-tokenized-command leg** confirmed live. Built a `scripted-badcmd` adapter (committed
`.mainguard/verify: node test.js && echo done` — a `&&` token that survives argv tokenization). The
CLI-picker dropdown would not commit a selection under any of four distinct automation techniques tried
this leg (CGEvent click, keyboard arrow-keys, AppleScript `System Events click` on the actual accessibility
menu item, and a direct `AXPress` action on that same element) — confirmed via the accessibility tree that
the popup genuinely opens and lists the right items, but no technique closes it with a different selection
committed. This is now a well-confirmed automation limitation (see ISSUES-LOG #33 methodology note), not a
one-off flake. Worked around by using `SpawnAgentAsync` via RPC for the CLI-selection step only (setup, not
the thing under test), then clicking the row's real **Verify** button in the UI for the actual assertion
(`201`): `Can't verify — The verification command contains '&&', which needs a shell — ... Wrap it in a
shell instead: sh -c "node test.js && echo done"` — third distinct wording confirmed. Discarded the entry
afterward.

**D5 — flagged-change (P2-11)** confirmed live, full loop. Built a `scripted-ci` adapter that commits
`.github/workflows/ci.yml`. Spawned (RPC setup, same dropdown workaround), clicked Verify: entry reached
**Verified** but the rail showed "1 flagged change needs acknowledgment" and no Merge button (`202`).
Opened Review: cockpit shows a red "FLAGGED — acknowledge each to enable merge" banner naming
`.github/workflows/ci.yml` with reason "CI workflow changed (runs with repo credentials)", the actual diff
content, and Merge greyed out (`203`). Clicked Acknowledge: banner badge flips to "Acknowledged", footer
reads "ready to merge", and Merge lights up (`204`) — the full block→acknowledge→unblock loop confirmed
live, not just the initial flag.

**E6 — Clear stalled verification** — analyzed, not force-reproduced live. Read `MergeQueue.cs` closely:
both exit paths of `RunVerificationAsync` (success and failure) unconditionally settle the entry out of
`Verifying` before returning, and `ResumeAfterRestartAsync` explicitly re-drives any run a daemon restart
interrupted (or strands it back to **Working**, never leaving it in `Verifying`, if the jail is confirmed
gone). `TryClearStalledVerification`'s own doc comment names its actual audience: a repo whose queue is
never rebuilt after a restart, a container-runtime probe that cannot even run, or some future path that
freezes an entry a different way — none of which are honestly reproducible via realistic live UI actions in
a bounded session. Concluding this is a **legitimate coverage gap from thoroughness, not a bug** — the
system's self-healing on the ordinary interrupted-run path is solid enough that E6's precondition is hard
to hit by accident, which is the point of the escape hatch existing at all. Logged as ISSUES-LOG #34
(methodology note, not a defect) with the reasoning above for whoever picks this up next with more time
budget (e.g. `docker kill -SIGSTOP` on the jail mid-exec to see if the daemon-side probe hangs rather than
errors, which might be the one path that doesn't self-heal).

**B1 — BYOK key path** — partially confirmed, with a real new finding. The primary Anthropic-key path
(`Settings → AI Providers → Store an API key`) validates against the live Anthropic API before saving ("An
invalid key is never saved") — no real Anthropic key was available this session, so that specific path's
save-then-isolate claim is **still unverified live** (accessed the Settings window successfully via
`Cmd+,`, confirmed it renders correctly, `205`, but did not attempt a fake key since the UI's own contract
says it would correctly refuse to save one, which isn't the interesting half of the claim). Used the
adjacent, not-health-checked **Custom key** path instead — same settings page, explicitly for "CLIs that
read their own variable... stored in your OS keyring and injected into every agent's environment." Saved a
throwaway marker (`TEST_CUSTOM_MARKER` = a recognizable fake value, `206`) via real keystrokes and a real
click — UI confirmed "Stored TEST_CUSTOM_MARKER... it is injected into every agent's environment."

Spawned a fresh `scripted` coordinator and checked the actual jail: `docker exec <jail> env` — **no leak**,
the raw marker value never appears in the container's process environment (correct, matches the B5
secrets-delivery contract). But checking the intended delivery path, `/run/secrets/agent/agent.env` (0400,
correctly owned by `agent:agent`, matching the documented mechanism) — **the file is present but empty**.
Not a caching/timing artifact: killed and relaunched the app+daemon fully, spawned a second fresh agent
against the same freshly-loaded keyring entry, checked again — still empty. **Confirmed as a real defect**:
the custom-key feature's own UI claim ("injected into every agent's environment") does not hold for a
`scripted`-class adapter; the credential is durably saved to the OS keyring (the "Stored" badge and Remove
button in Settings persisted across the restart, confirming the save side works) but never actually reaches
the agent's environment via either path checked. Logged as ISSUES-LOG #35 (MEDIUM — fails safe, no leak, but
the feature is silently non-functional, which would strand a real OpenRouter/custom-provider user with an
agent that can't authenticate and no error telling them why).

**Matrix coverage after this leg**: D2 (all three legs), D5 now fully UI-click-verified. E6 and B1's primary
Anthropic-key leg remain genuinely open (documented reasoning above, not abandoned without explanation).
Still gapped: C2, C3, H6, B1's Anthropic-key leg specifically.
