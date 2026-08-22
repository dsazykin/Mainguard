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

### 4. [FIXED — commit `7497202`] Stale queue clutter pushed new entries below the fold with a near-invisible scroll affordance
- **Steps:** 025-042 (the fix-attempt interlude)
- **This is NOT a data bug.** Exhaustive diagnosis (temporary instrumentation in `DaemonBackedOrchestrator.QueuePumpAsync`/`ReconnectLoopAsync` and `ControlCenterViewModel.OnChanged`, reverted before commit) proved conclusively: the daemon sends the correct full entry list, `DaemonBackedOrchestrator._queue` receives it, `ApplyQueueUpdate` populates it, `Changed` fires, `QueueRailViewModel.Refresh()` runs, and `Entries.Count` is correct (11/11) — all within milliseconds of a real spawn. A freshly spawned coordinator (`ac0a1c56...`) landed correctly in the ViewModel's collection every time.
- **What actually happened:** the Merge Queue panel's `ItemsControl` (`QueueRailView.axaml`) IS wrapped in a working `ScrollViewer` — but 5 leftover `Working` entries from earlier debugging sessions (dead jails, `docker rm -f`'d hours ago, never Discarded through the UI) sat at the top of the list, ahead of the fresh spawn, filling the visible area. The scrollbar thumb is thin/low-contrast and easy to miss — I myself initially concluded the queue was "stuck" and spent a long diagnostic detour before realizing I simply hadn't scrolled down far enough (and my first scroll attempt used the wrong on-screen coordinates, landing on the terminal panel instead of the queue panel, which looked like confirmation the panel didn't scroll at all).
- **Why this matters:** this is very plausibly exactly what the user experienced ("the spawned agent didn't appear in the merge queue") — not a data/wiring failure, but a real discoverability problem: a handful of accumulated stale/dead entries can bury a brand-new, fully-live entry below the fold with no visual cue ("3 more below", a badge count, sorting live entries to the top) that anything is hidden.
- Severity: medium-high — no data is wrong or lost, but the practical, observed effect ("my agent isn't in the queue") is identical to a real bug from the user's perspective, and is easy to reproduce simply by letting a few dead entries accumulate (which happens naturally from normal testing/experimentation).
- **Fixed in a later leg of this same pass (Step 013)**: reproduced a SECOND time on a truly fresh spawn against a confirmed-fresh binary (ruled out a `StreamChannel`-fix regression first, via a `stat`-timestamp check that caught the running app was stale — see Step 013), which made the mechanism obvious: `MergeQueue.Agents` returns stable dictionary-insertion order, and Merged/Rejected rows persist forever by design (E4/E5), so they permanently occupy the front of that order — bottom-of-list is where every fresh spawn lands, always. Fixed with `MergeQueueGrpcService.OrderForDisplay` — a stable partition (actionable states first, terminal last, relative order preserved within each group) — wired into `Snapshot()`. 3 new unit tests in `Mainguard.Server.Tests/QueueDisplayOrderTests.cs` (no daemon/Docker required) pass; full solution rebuild clean. Committed `7497202`, pushed. The remaining, NOT-fixed half of the original suggestion (an explicit "N more" / unread-count affordance, or auto-collapsing long-stranded entries into their own sub-section) is still a legitimate follow-up but no longer required for correctness — the actionable-first sort alone means a user opening the panel always sees live work first, without scrolling.

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

### 9. [OPEN, HIGH — real bug, C4] Composer Send on a jailless/stranded agent silently swallows the prompt
- **Steps:** 052-057
- A failed **Resume** click (branch already gone, adversarial E7 case) unexpectedly opened an Agent
  Document surface for that agent in the left rail (a new red-dot icon that wasn't there before —
  clicking it opens `Terminal`/`Agent diff`/`Plan`/`Staging` panels for `2664ef9b52...`, a stranded
  entry with no live sandbox). **Correction to prior-session belief:** the composer/document surface
  does render in phase-1 — the earlier finding that it "never renders" was specific to a different
  code path, not universal.
- **Action:** typed a real prompt ("test prompt for stranded agent") into the "Send a follow-up
  prompt" composer and clicked **Send**.
- **Expected** (per matrix C4): either it works (unlikely — no jail exists), or it refuses visibly —
  "the daemon input-lock surfaces as a `PermissionDenied` rendered under the composer."
- **Observed:** neither. The text field silently cleared, no toast, no inline error, no message in the
  Terminal panel, nothing in the Agent diff panel — a complete, silent no-op. This is exactly the
  failure mode the matrix's own design philosophy explicitly forbids ("never a silent nothing, a typed
  refusal") and it's a worse instance than the previously-known `SendPromptAsync` cancel-race (which at
  least sometimes reaches a real jail) — this is a UI affordance that is fully rendered, fully
  interactive, and fully inert with zero feedback for an agent with no sandbox at all.
- Severity: HIGH — a user has no way to know their prompt was dropped rather than received; on a
  stranded entry (which, per ISSUES-LOG #4, are common/easy to accumulate) this is a real trap.
- Not fixed this pass (non-blocking — did not prevent continuing other rows). Likely the same
  underlying composer/`SendPromptAsync` code path as the already-known cancel-race bug, but the
  complete absence of ANY error surfacing (not even a delayed one) suggests the jailless case may not
  be handled/guarded at all client-side, distinct from the race condition on a live jail.

### 10. [methodology note, not a Mainguard bug] A screen-point click landed on the Claude desktop app, not Mainguard
- **Step:** between 052 and 054
- A coordinate-conversion mistake (passed raw screenshot-pixel numbers directly as screen points,
  skipping the `/2 + origin` conversion `click_at.sh` normally applies) sent a click miles outside the
  Mainguard window, which happened to land on and focus the Claude desktop app instead, briefly
  surfacing this very session's own transcript. Caught immediately (the "finding" was obviously not a
  Mainguard surface), refocused Mainguard via `System Events … set frontmost`, recalibrated, continued.
  Not logged as a product defect; noted only so this walkthrough's provenance is honest about the slip.
  (The intermediate screenshot showing the Claude app's own transcript was removed rather than
  committed — not useful evidence and not something to publish.)

### 6. [OPEN, testing-methodology note, not a Mainguard bug] `System Events keystroke` drops characters on fast/long strings
- **Step:** 008
- Typing a full sentence via `osascript -e 'tell application "System Events" to keystroke "..."'` into the terminal dropped several characters/words (rendered as "a on-line comment... top o.js" instead of "a one-line comment... top of src/calc.js"). This is very likely an AppleScript/Accessibility-API typing-speed limitation, not a Mainguard input-handling bug — the terminal reliably received and rendered whatever it was actually sent, and the agent's own excellent handling of the corrupted prompt (asking for disambiguation rather than guessing wrong) is itself a positive data point.
- Noted for the runbook: future live-agent legs should type in smaller chunks or with inter-keystroke delay, or route through `SendPromptAsync` (once its own known drop-on-cancel-race bug is fixed) rather than raw `System Events keystroke` for long strings.

### 11. [CLOSED — commits `978db19` + `a972b02`] The merge-queue stream silently dies mid-session and never retries, showing "Nothing queued" while the data is intact
- **Step:** 014 (this leg's resume point)
- Found the app in a live, previously-running session (not freshly launched) showing **"Nothing queued."** in the Merge Queue panel with no coordinator running and no error banner — looked identical to a real empty/broken queue.
- **Ruled out as data loss**, decisively: `sqlite3 ~/.mainguard/mainguard-daemon.db` shows **26 total `MergeQueueRows`** (12 Working, 7 Merged, 2 Rejected, 1 Verified, 4 Discarded) — 17 of them for the exact `RepoHash` (`f467e17...`) the daemon's own `rpc.log` confirms is the currently bound repo (`e2e-fixture`, most recent `ProvisionRepo` call at 15:42:09 UTC). The persisted data was completely intact.
- **Root-caused to the client's queue stream pump, not the server:** `rpc.log` shows exactly one `StreamQueue` call after the crash-relaunch (see #12 below) — `rpc-begin` at 15:45:13.741 UTC, `rpc-end status=OK duration_ms=32`. `MergeQueueGrpcService.StreamQueue` (`Mainguard.Server/Services/MergeQueueGrpcService.cs:54-83`) only ever returns that fast via its `catch (OperationCanceledException)` teardown path — meaning the **client** cancelled the call ~32ms after opening it, before the daemon could push anything past the initial snapshot write (and even that snapshot write may not have completed in time). Confirmed this wasn't a one-off retry that then succeeded: **zero further `StreamQueue` log lines appear for the next 5m45s** of continuous session activity, while the agent-event pump's `ListAgents` polling kept succeeding once a minute the whole time (`rpc.log`, 15:44:53 through 15:50:53) — proving the daemon connection itself was healthy and only the queue pump specifically had stopped.
- **This contradicts `DaemonBackedOrchestrator.QueuePumpAsync`'s own documented contract**: it's wrapped in `ReconnectLoopAsync` (`DaemonBackedOrchestrator.cs:537-582`), which is supposed to retry every `ReconnectDelay` (2s) after ANY exit from the stream body — success or failure — until cancelled. A stream that cleanly completes (as this one did, `status=OK`) should have been retried within ~2 seconds, indefinitely, forever, per that method's own doc comment ("this is what makes an empty projection **live**"). It did not retry even once in over 5 minutes. This means either (a) `_queuePumpCts` was cancelled by some other code path around 15:45:13 without a matching restart (the rebind guard at `DaemonBackedOrchestrator.cs:401-411` only starts a new pump task when the *repo handle* changes or no pump exists yet — a redundant/duplicate rebind call with the *same* handle while a pump is already in flight would cancel nothing, so this alone doesn't explain it), or (b) the `Task.Run(() => QueuePumpAsync(...))` task itself silently faulted in a way `ReconnectLoopAsync`'s `catch (Exception)` does not catch (e.g. before entering the try, or an `OperationCanceledException` whose `ct.IsCancellationRequested` check evaluates true even though the *outer* `_cts` wasn't meant to be cancelled — worth checking whether `_queuePumpCts`'s linkage to `_cts` could itself already be cancelled at that point for an unrelated reason, e.g. app backgrounding/suspend on macOS).
- **Not fully root-caused to a single line** — ran out of investigative budget chasing the exact cancellation trigger (suspected, not confirmed: the window losing/regaining focus around the same timestamp, since bringing the app frontmost via `System Events` was one of the actions taken in this same window, but no `Activated`-style client code path was found that would plausibly re-trigger a repo rebind on focus change, so this is circumstantial, not proven).
- **Practical impact confirmed live**: this is indistinguishable from the user's original complaint ("couldn't even get into the merge queue") from inside the app — no error, no toast, no stale-data indicator, just a permanently empty panel that requires a full app restart to recover (confirmed: restarting cleanly restored all 7 `Working` entries correctly sorted, screenshot `087`).
- Severity: **HIGH** — this can strand a user's entire merge queue behind a silent, permanent, unrecoverable-without-restart client bug, mid-session, with zero indication anything is wrong (`DaemonClient.State` almost certainly still reports `Connected` since other RPCs work fine — matches the exact class of bug the `AgentPumpAsync` doc comment on the same file already warns about for a *different* pump: "the visible symptom is a list frozen at its last good snapshot, forever, with the app looking healthy").
- **Recommended follow-up** (not done this pass): add a regression test that cancels an in-flight `StreamQueueAsync` call via a fresh `_queuePumpCts` swap and asserts a new stream attempt starts within `ReconnectDelay`; instrument `QueuePumpAsync`/`ReconnectLoopAsync` with a debug log line on each retry attempt (currently: total silence, which is itself part of why this is hard to diagnose — nothing in any `.mainguard/logs/*.log` file recorded the client giving up).
- **Update, next leg (Step 015):** found and fixed a real, independent, structurally-related bug that is a strong contributing-cause candidate: `ControlCenterViewModel.ProvisionRepoAsync` unconditionally calls `ClearActiveRepo()` (which synchronously blanks the queue projection) on **every** repo-open call, including a redundant re-open of the already-active repo, then awaits a real RPC with a 5-minute deadline before the pump restarts — with zero UI indication anything is happening in the meantime. Fixed via `DaemonBackedOrchestrator.IsBoundTo` + a short-circuit in `ProvisionRepoAsync` (commit `978db19`), regression-tested, verified live against a fresh launch (screenshot `088`). **Left OPEN, not closed**: this fix removes a real, confirmed-live bug in the same family, but does not itself prove it was the exact trigger for the original #11 incident (whose specific cause — what called `OpenRepositoryAsync` again at 15:45:13 UTC — is still not identified). Re-test: leave a session running for 10+ minutes while periodically re-triggering a repo-open action (command palette "Open &lt;repo&gt;" on the already-open repo, `ReopenLastRepo`, etc.) and confirm the queue rail never blanks.

- **CLOSED, next leg (Step 016) — root-caused by elimination against the surviving daemon log, then fixed (commit `a972b02`).**

  **The reconnect loop was never the bug.** The prior leg's hypothesis (b) — "the `Task.Run(() => QueuePumpAsync(...))` task silently faulted in a way `ReconnectLoopAsync`'s `catch (Exception)` does not catch" — is **ruled out**, and proved so rather than argued: the two new `QueuePumpResilienceTests` drive a queue stream that (i) ends cleanly and (ii) faults, and both assert a second subscription reaches the rail's projection. Both **pass against the pre-fix code**. `ReconnectLoopAsync` retries exactly as its doc comment claims.

  **App backgrounding / suspend / `Dispose` is also ruled out**, from the log itself: `_queuePumpCts` is a *linked* child of the adapter's `_cts`, and so is `LoginHarvestPumpAsync` — whose one-minute `ListAgents` sweep is precisely the "polling kept succeeding" the original entry noticed (`rpc.log`, 15:39:53 → 15:51:53, every 60s on the shared connection, port 60920). If `_cts` had been cancelled that pump would have exited too. It did not, so `_cts` was alive and only the *queue-specific* child token was cancelled.

  **That leaves exactly one possible cancellation source, and it is not a race inside the orchestrator.** `_queuePumpCts` is cancelled in only two places: `SetActiveRepo` with a *different* handle — which immediately starts a replacement pump, and would therefore have written a new `StreamQueue` `rpc-begin` (none exists) — and `ClearActiveRepo()`, whose only caller in the entire repository is `ControlCenterViewModel.ProvisionRepoAsync`. So the stream was torn down by a repo-open call that then **never got back to `SetActiveRepo`**. The log's timing says the same thing unambiguously: `StreamQueue` *begins* at 15:45:13.741 and is cancelled at 15:45:13.765 — a `SetActiveRepo` and a `ClearActiveRepo` **23 ms apart**, i.e. one repo-open binding the pump while another tore it down.

  **The actual defect is that the pump's liveness depended on an external caller finishing a multi-step sequence,** and three separate paths let that sequence stop halfway. All three are now closed:
  1. **`ProvisionRepoAsync` ran concurrently with itself.** `MainWindowViewModel.OpenRepositoryAsync:775` starts it **fire-and-forget** (`_ = TryRegisterSyncRemoteAsync(repo.Path)`), so two opens a few hundred ms apart really do overlap — and both read `_lastProvisioned` before either wrote it, which defeats `978db19`'s same-repo short-circuit for *both* of them. The interleave is clear → clear → provision → bind → (the loser's clear lands after the winner's bind) → nothing rebinds. Now serialized through a `SemaphoreSlim`, so the second caller sees the first's finished binding and takes the no-op path. This is the 23 ms signature.
  2. **A failed RE-provision of the already-open repo left the adapter unbound forever.** The clear has already run, and that path never reaches `SetActiveRepo` again — so one transient failure (a daemon hiccup, or a client-side channel fault that never even reaches the wire, which would explain why no `ProvisionRepo` line accompanies the 15:45:13 teardown) permanently blanked a working queue, restart-only recovery, exactly as observed. The previous binding is now re-armed on failure — the handle is the repo's content hash and is stable across provisions — handing the pump back to its own reconnect-forever loop. Guarded on an unchanged repo path so the B4 regression (a failed switch to repo B must not resurrect repo A's queue) stays fixed.
  3. **`MainWindowViewModel` registered the sync remote *before* binding the queue.** `SyncRemoteRegistrar.Register` is real host-side git work and can throw (a held `.git/index.lock`, a read-only/malformed config); the throw fell to the outer `catch`, toasted, and left the already-torn-down pump dead for the session. Observing the queue never needed the remote — only *merging* does — so the bind now goes first.

  Two smaller things found in the same read and fixed alongside: `ApplyQueueUpdate` raised `Changed` **unisolated** (unlike `ApplyAgentEvent`, which was hardened against exactly this), so a throwing UI subscriber propagated out of the `await foreach` and cycled the stream every `ReconnectDelay`; and `ControlCenterViewModel.Dispose` was missing the `_queue.Changed -= OnChanged` half of its constructor's subscription, so a disposed VM stayed wired to the live queue stream.

  **Regression coverage:** `Mainguard.Tests/QueuePumpResilienceTests.cs` (3 tests — queue-pump re-subscribe after a clean end, after a fault, and survive-a-throwing-subscriber; the first two document that the loop was already correct, the third fails before) plus 2 added to `RepoProvisioningHonestyTests` (concurrent opens are serialized so the second sees the first's binding; a failed re-provision of the same repo restores the binding). **3 of the 5 verified to fail against the pre-fix code**, by temporarily neutering the fix and re-running.

  **Residual unknown, and why it is no longer load-bearing:** which of paths 1–3 fired at 15:45:13 cannot be distinguished from the surviving log — the client emits no line of its own, and a client-side provision failure leaves no daemon-side trace at all. All three are closed, and the failure mode they shared (a torn-down pump with no one left to restart it) is now closed at the source in two independent ways. **Not reproduced live** by GUI automation: the winning interleave needs two repo-opens inside a ~150 ms `ProvisionRepo` window, which is not reliably hand-drivable through the UI — the deterministic fails-before unit tests are the stronger evidence here, and are what would catch a regression. **Follow-up worth doing anyway (not blocking):** the client still logs *nothing* when a pump binds, unbinds or gives up, which is the single biggest reason this took three legs to pin — one debug line per re-subscribe and per `ClearActiveRepo` would have answered it in minutes.

### 12. [CONFIRMED — separate finding] The Mainguard.app UI client crashed (SIGABRT) once during this session
- **Step:** 013/014 boundary
- `~/Library/Logs/DiagnosticReports/Mainguard-2026-08-20-173042.ips`: `Mainguard` (the UI client, not the daemon) crashed with `EXC_CRASH`/`SIGABRT` at 2026-08-20 17:30:42 local time — the crash trace shows `IL_Throw` → `DispatchManagedException` → `abort()`, i.e. an **unhandled .NET managed exception** reached the top of the stack and crashed the process, not a native fault. This was the only Mainguard-attributed crash report from today (a separate `dotnet_...plist` crash-reporter entry from 13:37 is very likely an unrelated `dotnet test` process crash, not the product).
- The daemon's own `lifecycle.log` shows a normal "shutdown requested — draining" / "stopped" pair at 15:30:28 UTC (matching local 17:30:28, 14s before the crash timestamp) followed by a relaunch at 15:38:50 UTC — consistent with the client crashing and something (the OS or a supervising script) relaunching both processes ~8 minutes later, though this pass did not confirm whether that gap was manual or automatic.
- **Not root-caused** — the release build strips managed exception type/message from the native crash report, so the actual exception (`NullReferenceException`? something UI-thread-specific?) is not recoverable from this artifact alone without a debug build or attached debugger at the time. Flagging as a real, confirmed crash for follow-up; possibly related to finding #11 above (a crash mid-stream could plausibly be *what* orphaned the queue pump in the first place, though the timestamps don't overlap — the crash was at 17:30:42, the dead-stream `StreamQueue` call was at 17:45:13, ~15 minutes later, i.e. after a full app restart already happened in between — so they are almost certainly independent incidents, not cause-and-effect).

### 13. [CONFIRMED — real regression, HIGH] Rejected queue entries never appear in the Merge Queue panel at all
- **Step:** the live E5 Reject pass this leg (Resume → Verify → Review → Reject on entry `506a60e6e700471aa945fdc53851f492`, real UI clicks throughout).
- **What happened:** `RejectEntry` succeeded correctly — `rpc.log` confirms `RejectEntryResponse { rejected=True, rejected_by=os:danielsazykin, rejected_at=... }` — and `sqlite3 ~/.mainguard/mainguard-daemon.db` confirms the row is durably persisted as `State='Rejected'`. But the entry vanished from the Merge Queue panel entirely instead of showing as a `Rejected` row. This directly contradicts E5's own spec ("stays on the stream, unlike Discard") and contradicts this SAME walkthrough's own Step 004, which observed a *different* pre-existing Rejected entry (`b5224606390f4de89b205f6982502c67`) rendering correctly in the rail earlier in the session.
- **Ruled out data loss and stale-push theories, decisively:** `SELECT State, count(*) FROM MergeQueueRows WHERE RepoHash=... GROUP BY State` returns `Discarded|4, Merged|5, Rejected|2, Working|6` — both Rejected rows are durably there. Fully quit and relaunched the app + daemon (a cold `open`, not a soft reconnect) and reopened the repo from scratch: the panel STILL shows exactly 8 rows (6 Working + 2 Merged) and zero Rejected rows on a fresh cold snapshot, ruling out a live-push propagation gap (the class of bug #11 was) — this is either a hydration/query bug or a rendering bug, not a missed event.
- **Traced the render path without finding the drop point:** `MergeQueue.Agents` (`Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs:327-333`) only excludes `Discarded`, not `Rejected`. `MergeQueueGrpcService.OrderForDisplay` (`Mainguard.Server/Services/MergeQueueGrpcService.cs:573-576`) only reorders Merged/Rejected to the back, never filters them out. Client-side `DaemonBackedOrchestrator.ApplyQueueUpdate` (`Mainguard.Agents.UI/Services/DaemonBackedOrchestrator.cs:793+`) parses `entry.State` generically via `Enum.TryParse<WorkerMergeState>`, no exclusion. `QueueRailViewModel.Refresh()` (`Mainguard.Agents.UI/ViewModels/QueueRailViewModel.cs:51+`) iterates the full snapshot with no state filter either. None of the four layers I read contain an explicit "drop Rejected" line — the bug is either in server-side DB→memory rehydration (not yet located — grepped `MergeQueueProvisioner.cs` for the hydration path and didn't find it in the time available) or somewhere not yet inspected.
- **Note the two known-Rejected rows differ in one respect that might matter**: `b5224606390f4de89b205f6982502c67` (rendered fine in Step 004, timestamp `2026-08-18`) predates today's `7497202` ordering fix; `506a60e6...` (missing now, timestamp `2026-08-22` today) postdates it. Worth checking directly whether `7497202`'s `OrderForDisplay` — or the `Snapshot()` method around it — was touched in a way that's order-safe but not inclusion-safe, and whether `b5224606...` would STILL render today if re-checked (not re-verified this leg — a fast, valuable next step).
- Severity: **HIGH** — Reject is supposed to leave a permanent, visible record (that's the whole point of it being different from Discard); if rejected work silently disappears from the panel a reviewer has no way to know a rejection ever happened without going to the database directly, and the user's own emphasis this session was specifically on "already-merged/queue-state entries going missing" as the class of bug that started this whole pass.
- **Not fixed this leg** — per the standing instruction, a bug this structurally unclear (four ruled-out layers, no located drop point) needs a dedicated deeper investigation rather than a rushed guess; flagged prominently in the final report for a follow-up pass, likely by the same kind of focused investigation that closed #11.
