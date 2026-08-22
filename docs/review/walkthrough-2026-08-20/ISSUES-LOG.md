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
- **Fixed in a later leg of this same pass (Step 013)**: reproduced a SECOND time on a truly fresh spawn against a confirmed-fresh binary (ruled out a `StreamChannel`-fix regression first, via a `stat`-timestamp check that caught the running app was stale — see Step 013), which made the mechanism obvious: `MergeQueue.Agents` returns stable dictionary-insertion order, and Merged/Rejected rows persist forever by design (E4/E5), so they permanently occupy the front of that order — bottom-of-list is where every fresh spawn lands, always. Fixed with `MergeQueueGrpcService.OrderForDisplay` — a stable partition (actionable states first, terminal last, relative order preserved within each group) — wired into `Snapshot()`. 3 new unit tests in `Mainguard.Server.Tests/QueueDisplayOrderTests.cs` (no daemon/Docker required) pass; full solution rebuild clean. Committed `7497202`, pushed. The remaining, NOT-fixed half of the original suggestion (an explicit "N more" / unread-count affordance, or auto-collapsing long-stranded entries into their own sub-section) is still a legitimate follow-up but no longer required for correctness — the actionable-first sort alone means a user opening the panel always sees live work first, without scrolling. **Update 2026-08-22:** that deferred half went on to cause a second false HIGH report (#13 — a Rejected row read as vanished when it was rendering below the fold), so the count cue was built after all: the rail header now carries `"N in play · N in history (merged/rejected, below)"` (commit `c1e4c3e`).

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
- **Still open. Re-checked on the 2026-08-22 #18/#19/#20 leg and nothing new surfaced** (a look, not a
  hunt — the reconciliation work was the priority): `~/Library/Logs/DiagnosticReports/` holds **no**
  Mainguard-attributed crash report at all since the 08-20 one, so neither the 08-22 disappearance in
  #19's leg nor anything since produced an artifact. The daemon's `rpc.log` exceptions around the client
  restarts are all `IOException: The client reset the request stream` / `The request stream was aborted`
  out of `TerminalGrpcService.PumpBoundAsync` — ordinary client-disconnect teardown of an attach stream,
  the *consequence* of an app going away rather than a cause, plus one `Input/output error` from reading
  a PTY whose process was already gone. Worth noting for whoever picks this up: a `SIGABRT` with no crash
  report and a clean process disappearance are different symptoms, and #19's leg saw the latter — that
  pattern is more consistent with the process being terminated from outside (or exiting a main loop) than
  with an unhandled managed exception, which is what produced the 08-20 report.

### 13. [CLOSED — commit `c1e4c3e`. NOT data loss, and not a rendering failure: an ORDERING defect + a missing overflow cue] Rejected queue entries appeared to vanish from the Merge Queue panel
- **Step:** the live E5 Reject pass this leg (Resume → Verify → Review → Reject on entry `506a60e6e700471aa945fdc53851f492`, real UI clicks throughout).
- **What happened:** `RejectEntry` succeeded correctly — `rpc.log` confirms `RejectEntryResponse { rejected=True, rejected_by=os:danielsazykin, rejected_at=... }` — and `sqlite3 ~/.mainguard/mainguard-daemon.db` confirms the row is durably persisted as `State='Rejected'`. But the entry vanished from the Merge Queue panel entirely instead of showing as a `Rejected` row. This directly contradicts E5's own spec ("stays on the stream, unlike Discard") and contradicts this SAME walkthrough's own Step 004, which observed a *different* pre-existing Rejected entry (`b5224606390f4de89b205f6982502c67`) rendering correctly in the rail earlier in the session.
- **Ruled out data loss and stale-push theories, decisively:** `SELECT State, count(*) FROM MergeQueueRows WHERE RepoHash=... GROUP BY State` returns `Discarded|4, Merged|5, Rejected|2, Working|6` — both Rejected rows are durably there. Fully quit and relaunched the app + daemon (a cold `open`, not a soft reconnect) and reopened the repo from scratch: the panel STILL shows exactly 8 rows (6 Working + 2 Merged) and zero Rejected rows on a fresh cold snapshot, ruling out a live-push propagation gap (the class of bug #11 was) — this is either a hydration/query bug or a rendering bug, not a missed event.
- **Traced the render path without finding the drop point:** `MergeQueue.Agents` (`Mainguard.Agents/Agents/Orchestrator/MergeQueue.cs:327-333`) only excludes `Discarded`, not `Rejected`. `MergeQueueGrpcService.OrderForDisplay` (`Mainguard.Server/Services/MergeQueueGrpcService.cs:573-576`) only reorders Merged/Rejected to the back, never filters them out. Client-side `DaemonBackedOrchestrator.ApplyQueueUpdate` (`Mainguard.Agents.UI/Services/DaemonBackedOrchestrator.cs:793+`) parses `entry.State` generically via `Enum.TryParse<WorkerMergeState>`, no exclusion. `QueueRailViewModel.Refresh()` (`Mainguard.Agents.UI/ViewModels/QueueRailViewModel.cs:51+`) iterates the full snapshot with no state filter either. None of the four layers I read contain an explicit "drop Rejected" line — the bug is either in server-side DB→memory rehydration (not yet located — grepped `MergeQueueProvisioner.cs` for the hydration path and didn't find it in the time available) or somewhere not yet inspected.
- **Note the two known-Rejected rows differ in one respect that might matter**: `b5224606390f4de89b205f6982502c67` (rendered fine in Step 004, timestamp `2026-08-18`) predates today's `7497202` ordering fix; `506a60e6...` (missing now, timestamp `2026-08-22` today) postdates it. Worth checking directly whether `7497202`'s `OrderForDisplay` — or the `Snapshot()` method around it — was touched in a way that's order-safe but not inclusion-safe, and whether `b5224606...` would STILL render today if re-checked (not re-verified this leg — a fast, valuable next step).
- Severity: **HIGH** — Reject is supposed to leave a permanent, visible record (that's the whole point of it being different from Discard); if rejected work silently disappears from the panel a reviewer has no way to know a rejection ever happened without going to the database directly, and the user's own emphasis this session was specifically on "already-merged/queue-state entries going missing" as the class of bug that started this whole pass.
- **Not fixed this leg** — per the standing instruction, a bug this structurally unclear (four ruled-out layers, no located drop point) needs a dedicated deeper investigation rather than a rushed guess; flagged prominently in the final report for a follow-up pass, likely by the same kind of focused investigation that closed #11.

#### Resolution (2026-08-22, dedicated follow-up leg) — **the row was on screen the whole time**

- **There is no drop point, which is why four layers of reading did not find one.** `MergeQueue.Agents`,
  `OrderForDisplay`, `Snapshot`, `ApplyQueueUpdate` and `QueueRailViewModel.Refresh` are all
  inclusion-safe exactly as the previous leg concluded. Scrolling the live panel with a real scroll-wheel
  event found `506a60e6…` rendering correctly as **Rejected / "rejected in review"** — as the **13th of
  13** rows, four scroll-lines below the fold.
- **The count that should have caught this earlier:** the DB holds 13 non-Discarded rows for the repo
  (6 Working + 5 Merged + 2 Rejected). The panel fits **8**. The leg above counted the 8 visible rows
  (6 Working + 2 Merged), matched "2 Merged" against "5 Merged in the DB" without noticing the shortfall,
  and read the difference as Rejected-specific loss. It was not Rejected-specific: **three Merged rows
  were equally "missing"**, by the same mechanism. That arithmetic is the tell, and it is worth
  remembering — a partial count is not an absence.
- **`7497202` WAS implicated, but as the other half of its own fix, not as a filter.** It sorts actionable
  entries ahead of terminal ones (correct, and still correct), but leaves the terminal group in
  `MergeQueue.Agents`' dictionary order — which is **spawn order, not decision order**. So a branch
  rejected thirty seconds ago sorts behind every terminal row ever spawned before it: last on any repo
  with history. That also explains the "one renders, one doesn't" observation exactly — `b5224606…` was
  spawned early, so it sits near the FRONT of the terminal group and stayed visible; `506a60e6…` was
  spawned late and went to the very back. Nothing about the newer row's format, DTO shape or round-trip
  differed at all.
- **Why it reads as a HIGH data bug from the chair:** the human presses Reject, the row leaves the visible
  panel at that instant (it moves from the actionable group to the bottom of history), and the rail's
  scrollbar is a thin low-contrast thumb with no "N more" cue — the un-fixed half of **#4**, now hiding
  the row the human had just acted on. Two separate false HIGH reports (#4's first diagnosis, then this
  one) have now come out of that one missing affordance.
- **Fixed** in `c1e4c3e`: (1) `MergeQueue.LastChangedAt` mirrors the row's persisted `UpdatedUtc` in memory
  and rehydrates it on restart (never restamps — a restart must not flatten history order); (2)
  `OrderForDisplay` takes that as an optional decision clock and sorts the terminal group **newest verdict
  first** (unknown timestamps keep their insertion position at the back — never dropped; the actionable
  group stays oldest-first, because it is a work queue); (3) `QueueRailViewModel.CountText` renders
  `"6 in play · 7 in history (merged/rejected, below)"` under the rail's `main <sha>` line, so a list
  taller than its viewport says so.
- **Tests:** 2 new `QueueDisplayOrderTests` units (a fresh verdict heads history; terminal rows with no
  timestamp survive at the back) + `QueueEntryLifecycleTests.RejectEntry_PutsTheFreshVerdictAtTheHeadOf…`,
  an end-to-end positional assertion driving two real `RejectEntry` RPCs and reading the order off
  `StreamQueue`. 16 queue-display/lifecycle tests, 57 server merge-queue-area tests and 84
  client-side queue/rail tests all green; full solution builds clean.
- **Verified live** on the rebuilt Pro app (cold relaunch of app + daemon, repo reopened, real scroll
  events): header reads `6 in play · 7 in history (merged/rejected, below)`, and `506a60e6…` **Rejected**
  is now the first history row, immediately under the last Working entry.
- **Follow-up still open (small):** the fix makes the newest verdict the top of history, but with enough
  actionable rows a fresh verdict can still start below the fold. The count line now tells the user rows
  exist down there; an explicit "jump to history" or a sticky section header would tell them where. Not
  required for correctness.

## 14. [Not a bug — resolved by relaunch] App's own window had closed with the process still running

- **Symptom:** resuming this leg, `com.mainguard.app` (PID 18046) was alive and reported as the frontmost
  process, but `System Events` returned "Can't get window 1 of process Mainguard — Invalid index" —
  zero AX windows. `CGWindowListCopyWindowInfo` still listed stale window entries for it, all
  `onscreen=false`. Cost real time to diagnose: the visible screen content (a Terminal window running
  this very `claude --continue` session) was mistaken at first for the app's own embedded terminal panel,
  since Mainguard being "frontmost" with literally nothing to draw let whatever was behind it show through.
- **Root cause, as far as it's knowable from outside:** the app's main window had been closed (or crashed
  its window without killing the process) sometime after the prior leg's live-verification pass — not
  reproduced deliberately, so the exact trigger is unknown. Clicking its Dock icon (the standard macOS
  "reopen main window" request) did **not** bring a window back, which is a real, if minor, gap: a
  single-window app should normally respond to the Dock-click reopen event.
- **Not investigated further** (out of scope for this pass — no crash log correlated, no repro attempt):
  logged here so the "Dock-click doesn't reopen the window" behavior is on record as worth checking
  separately, but NOT counted as a confirmed bug — could equally be expected behavior for this app's
  window-management model, which wasn't verified either way.
- **Fixed forward by:** `pkill` + `open` (clean relaunch). Confirmed clean recovery — repo picker
  appeared with a working "Reopen Last Repository?" prompt, reopened the `e2e-fixture` repo correctly,
  Coordinator panel loaded with the `6 in play · 7 in history` header from `c1e4c3e` rendering correctly.

## 15. [Confirmed via real UI click] Kill-switch freeze/unfreeze button itself works correctly (with 0 live jails)

- Clicked the sidebar kill-switch icon directly (previously only exercised via RPC). Engage: banner
  reads "All agents paused. The merge queue is frozen. Nothing was lost — resume when ready." /
  "queue frozen · 0 agents paused" (0 because the 5 stale `Working` rows in this fixture are jailless
  leftovers with no live container — expected, not a bug). Clicking the same icon again cleanly cleared
  the banner and returned the icon to its normal state.
- **This does NOT re-test the already-confirmed HIGH bug** (kill-switch Resume never un-pausing a jail
  that was actually paused — see the earlier entry in this log) — that needs a live jail, which this
  fixture's stranded entries don't have. Confirming the button's own freeze/unfreeze UX is correct with
  zero paused jails is a real, separate, positive result — logged as such, not conflated with the open bug.
- **Follow-up still needed:** reproduce the actual jail-never-resumes case through the real UI button
  (not RPC) with a genuinely live jail, to fully close that row.

## 16. [Confirmed via real UI click] Theme switching (H7)

- `View > Theme` menu correctly lists System / Midnight Loom / Daylight Loom / Graphite / Atelier.
  Selected Daylight Loom: entire app switched to its light palette immediately (background, sidebar,
  text all correct, no stale-dark-token bleed observed). Selected Graphite: dark palette applied
  correctly. Restored Midnight Loom. No visual defects observed in the two swaps performed.
- Atelier and a full System-theme (OS-follow) check were not exercised this leg — minor remaining gap,
  low risk given the two swaps tested were clean.

## 17. [CLOSED — commit `ee9be50`] Kill-switch Resume does NOT un-pause a real, currently-tracked agent's jail

This closes entry #15's "follow-up still needed" — reproduced end-to-end via real clicks, no RPC, no
orphan-jail ambiguity.

- **Setup honesty first:** before this test, the two live docker jails already running in this session
  (`...aab43299...`, `...506a60e6...`) were checked and confirmed to be **orphans from earlier daemon
  restarts** — `docker inspect .Created` on both predates the current daemon process's start time
  (`ps -o lstart`) by several minutes. That's why the Resource Monitor legitimately showed **"0 agents"**
  and the kill switch legitimately paused **"0 agents"** against them (screenshots
  `148-killswitch-engage-orphan-fixture-0-paused.png`, `149-resource-monitor-zero-agents-despite-2-orphan-jails.png`)
  — not a bug, just daemon restarts leaking untracked containers with no reconciliation on startup. Logged
  as its own minor finding below (#18); not conflated with the real bug this entry is about.
- **To get a clean, tracked jail:** selected `claude-code 2.1.234` in the real CLI dropdown and clicked
  **Start coordinator** for real. It spawned successfully, authenticated as the already-logged-in account
  ("Welcome back Daniel!", Opus 5 / Claude Max / `daniel.sazykin@gmail.com`'s Organization), and the
  Merge queue header incremented `6 in play` → `7 in play` live (screenshot
  `151-real-claude-code-coordinator-spawned-live-ui.png`). Resource Monitor correctly showed
  **"1 agents"**, `claude-code` / `Working` / `2% CPU` / `0.5 GB RAM` (`152-resource-monitor-1-agent-tracked-correctly.png`)
  — confirms Resource Monitor itself is NOT broken; it was correctly reporting zero for zero tracked
  agents earlier.
- **Engage, via the real sidebar icon, against this real tracked jail:** `docker inspect` before the
  click: `Paused=false`. Clicked Engage. `docker inspect` immediately after: **`Paused=true`**
  (`153-killswitch-engage-real-tracked-jail.png`). Engage half confirmed working correctly for a real
  agent.
- **Resume, via the same real sidebar icon:** clicked it a second time. The Coordinator panel's freeze
  banner cleared (queue-level freeze lifted — `155-coordinator-banner-cleared-jail-still-paused.png`).
  But `docker inspect` immediately after, and again 3+ seconds later, both still read **`Paused=true`**.
  The Resource Monitor row itself shows this too, not just Docker: state = **`Paused`**, `0% CPU`, task
  column reading **"Kill switch engaged — jail paused, terminal input severed (recoverable)."**
  (`154-killswitch-resume-ui-shows-paused-recoverable.png`) — the UI's own copy claims "(recoverable)"
  while the click that's supposed to recover it silently does nothing to the container.
- **Net result:** the queue-level freeze is lifted (Engage/Resume toggles that correctly), but the actual
  agent is left **permanently paused** — Working became Paused and never came back, confirmed by both
  Docker ground truth and the app's own Resource Monitor row agreeing with each other. This is the exact
  structural gap already root-caused via RPC in an earlier leg (`KillSwitch.cs`/`IKillTarget` has no
  unpause path — `Resume()` only clears the merge-queue freeze flag, nothing fans out an unpause to the
  paused containers), now proven through the real UI end to end with no possible orphan-jail confound.
- **No in-app recovery** — same as previously found: the per-agent `Unpause` RPC path refuses ("this
  agent isn't human-paused", since kill-switch pause and human pause are correctly modeled as distinct
  by design) and there is no other exposed control. Only a raw `docker unpause` from outside the app (or
  a daemon restart, which then orphans it per #18) recovers a killed agent today.
- **Environment left in this state (at the time of the finding):** the real jail spawned this leg
  (`mainguard-f467e1708a75-f1574a0ba9b443ffa2a5b2f9345df622`) was left genuinely paused by this test —
  `docker ps` showed it `Up ... (Paused)`, recoverable only by a manual `docker unpause`/`docker rm -f`.
  That sentence is the bug, written down.

### FIXED — commit `ee9be50`

`IKillTarget` gained `UnpauseAsync`, and `KillSwitch.ResumeAsync` is now the real mirror of
`EngageAsync`: it remembers the kill epoch's fan-out set, releases exactly those agents under the same
RT-D4 deadline the stop uses, then clears the freeze flag in a `finally` — an engine that refuses to wake
one jail must never *also* leave the operator behind a permanently frozen queue. An agent whose release
could not be confirmed comes back `ResumeFailed` and stays in the ledger, so pressing Resume again retries
exactly it. The release is audited (`killswitch_resume`), with the same RT-D3 never-block-on-the-store
posture as the kill.

**The human-pause distinction was preserved, not weakened** — this entry's own observation that the
per-agent `Unpause` RPC refusal is BY DESIGN drove the shape of the fix. The pause stays unconditional
(MG-39(a): containment is never negotiable); only the *release* is conditional. `SandboxKillTarget` keeps
a per-agent **causation ledger** of what it actually transitioned — which containers it paused, whether it
was the party that took the terminal lock / closed the leader's input gate — and reverses only those
entries. Causation is decided by engine STATE, never by matching Docker's error text: a jail already
frozen when the stop fires answers 409 to the second pause and the probe that follows confirms it is
contained, so it is now reported as `Paused` (it used to be mislabelled `Unresponsive`/`PauseFailed`) and
is deliberately *not* recorded as the kill switch's. The `HumanPauseLedger` is re-consulted at release
time as well, which wins the race where a human pause lands while the kill's own pause call is in flight.
Net effect: a human-paused agent goes through a whole Engage/Resume cycle and comes out still paused, and
the ledger still says human-paused.

The terminal sever had the identical asymmetry and got the identical treatment. The original
"deliberately no un-containment" note in `SandboxKillTarget` was protecting a real case — a managed
worker's terminal is locked at *spawn* as a role property, and a blanket unlock would hand an
operator-locked worker a typeable terminal — so the ledger records the lock/gate only when the kill switch
was the party that took them. That case is now honoured precisely instead of by refusing to recover at all.

The misleading UI copy is gone with it: the Resource Monitor row now reads "jail paused, terminal input
severed. Resume to recover." on a jail the kill switch owns, "this jail was already paused; it stays
paused after Resume." on one it does not, and "Resume FAILED (…) — the jail is STILL paused. Press Resume
again." when a release genuinely did not land. `ResumeKillResponse` carries per-agent counts, so `resumed`
now means the whole release succeeded, jails included.

**Evidence.** `Mainguard.Server.Tests/KillSwitchResumeDockerTests.cs` (RequiresDocker) is the end-to-end
claim that was false before, with Docker as the only witness: a real jail, real `SandboxKillTarget`, real
`DockerSandboxEngine`; engage → `.State.Paused=true`, resume → `.State.Paused=false`, terminal lock
released, row back to `Working`. A second Docker leg pins the arbitration (human-paused jail still paused
after the cycle, while the killed one comes back). Both green against a real OrbStack daemon. Every other
kill-switch test ran over a fake engine — which is exactly why this survived. Unit coverage added for the
release fan-out, idempotence, retry-after-failure, the spawn-time lock surviving the cycle, a vanished
container, and the double-Engage ledger merge. Full non-Docker `Mainguard.Server.Tests`: 568 passed,
0 failed.

**Not re-verified through live UI clicks this pass** — the running daemon at the time of the fix was an
older Release payload actively driving a live coordinator jail, and restarting the app to test would have
destroyed that session (and orphaned the jail per #18). The Docker-tier test exercises every layer the bug
lived in; the only link above it is the `KillSwitchService.Resume` RPC and the sidebar toggle, both of
which were already observed working for the freeze half in this walkthrough. Worth one live re-click on
the next leg for completeness, but not a gap in the fix.

## 18. [CLOSED — commit `67b9cc1`] Daemon restarts orphan any jails they had spawned — no reconciliation on startup

- Both live docker jails found at the start of this leg predate the current daemon process by several
  minutes (`docker inspect .Created` vs. `ps -o lstart` for the daemon PID) — they're leftovers from a
  daemon instance that existed before this leg's session (most likely from the `pkill`+`open` recovery in
  entry #14, or an earlier restart). The current daemon has zero record of them: `ListAgents`-backed
  surfaces (Resource Monitor, kill switch) correctly show/act on "0 agents" for them, which is *honest*
  given the daemon's own bookkeeping — but the underlying gap is real: **nothing reconciles Docker's
  actual state against the daemon's tracked-agent state on startup**, so a restart silently leaks any
  jails the previous process had running. They keep consuming CPU/RAM/disk indefinitely with no UI
  visibility and no automatic cleanup.
- This is the same pattern noted as an environmental aside in the original `findings.md` from an earlier
  session ("Two orphan mainguard-agent docker jails found... not tracked by the live daemon's agent
  list") — now precisely root-caused (daemon-restart-without-reconciliation) rather than just observed.
- Not fixed this leg (architectural — daemon startup would need to enumerate `docker ps --filter
  label=mainguard.role=agent` and either adopt or reap anything it doesn't recognize). Flagged for a
  dedicated pass alongside #17, since a real unpause fan-out (#17's fix) and orphan reconciliation (this
  one) touch the same `KillSwitch`/sandbox-lifecycle code and are worth doing together.
- **Still open after #17's fix (`ee9be50`), and now the *only* way a killed jail stays stuck.** #17's
  causation ledger lives in the daemon's memory, so a daemon that dies while the kill switch is engaged
  loses the record of what it froze along with the record of the jails themselves — the restarted daemon
  neither adopts nor releases them. Orphan reconciliation on startup is what closes that last hole; it is
  a smaller job now that the release path itself exists.

### FIXED — commit `67b9cc1`, and the diagnosis above was half right in a misleading way

- **"No reconciliation on startup" was not true — there were two, and neither wrote anywhere the UI
  reads.** `SwarmReconciler` (P2-08) has always run at boot, listed `mainguard.agent`-labelled containers
  and *adopted* orphans; `LeaderReattachTask` has always reconciled the durable PTY leader registry. But
  `SwarmReconciler` writes to the SQLite **`ExpectedAgents`** table and the leader task to
  `leader.json` — while `ListAgents`, `StreamAgentEvents`, the Resource Monitor and the kill switch are
  all projections of **`AgentSessionStore`**, which is a plain in-memory `Dictionary` that nothing has
  ever repopulated. The orphans were being adopted the whole time, into a book nobody reads. That is why
  the surfaces were "honest" about zero agents and why the jails still leaked.
- **The fix** is `Mainguard.Server/Runtime/AgentSessionReconciler.cs` + its `BackgroundService`: at
  startup and every 30 s it adopts live jails the session store has no record of, corrects state toward
  Docker, and marks a session whose container is gone `Unresponsive`. It **destroys nothing** — no
  container is stopped or removed. Reaping was considered and rejected: the labels identify every jail's
  repo and agent, so adoption is always possible, and this area has already paid once for a boot pass
  that swept user work silently (see `SwarmReconcileTask`'s own remarks). A stopped-but-present jail is
  also left alone, because the engine re-starts those by name and removing one destroys a resumable
  session. Adoption is gated on this daemon hosting the repository's bare mirror, since the container
  engine is machine-wide.
- **A second, worse defect found on the way in.** Docker reports a *paused* container as `State ==
  "paused"`, not `"running"` — and `DockerAgentLister` set `Running = (State == "running")`, which both
  older reconcilers read as "still here". So a daemon restart while **any** agent was paused declared it
  dead, force-removed its worktree and reaped its PTY. A restart during an engaged kill switch destroyed
  exactly the work the emergency stop exists to preserve. `AgentContainerState` now carries `Paused` and
  a computed `Live`, and both reconcilers read `Live`.
- **Adoption also needed the jail to say what it is.** Containers carried only repo/agent/`role=agent`,
  so an adopted coordinator came back anonymous. `mainguard.kind` and `mainguard.agent.role` are now
  stamped at create. **Known limit:** a jail created *before* this change has neither label and adopts as
  a role-less `unknown` worker — visible, monitorable and stoppable (which is this issue), but not
  re-bound as the coordinator. New jails do not have that gap.
- **Verified live, on the real thing.** The orphan `mainguard-f467e1708a75-f1574a0ba9b443ffa2a5b2f9345df622`
  from #19/#20 was still up and still invisible (`ListAgentsResponse { agents=[] }` in `rpc.log` while
  the container had been running 51 minutes). A production daemon built from this commit, started against
  the real data root, logged on boot:
  `agent-session reconcile: adopted=f1574a0ba9b443ffa2a5b2f9345df622 corrected=none lost=none` — while
  the OLD boot pass in the same startup still said `swarm reconcile: … adopted=none`, which is the
  "adopted into the wrong book" diagnosis printed side by side. Also covered by
  `AgentSessionReconcileDockerTests` (RequiresDocker, real containers) and
  `AgentSessionReconcileTests` (19 unit cases incl. the engine-unreachable case).

## 19. [CLOSED — commit `67b9cc1`] Coordinator panel shows "No coordinator running" after an app relaunch, despite a live, correctly-tagged coordinator agent

- Repro: the app process was found to have exited between two `ps aux` checks a few seconds apart (no
  crash report generated in `~/Library/Logs/DiagnosticReports/` — this is a *different* symptom from #14's
  "alive but zero windows"; here the process was simply gone). Recovered with `open
  .../Mainguard.app`. On the fresh launch, clicked "Reopen" on the "Reopen Last Repository?" prompt
  (`157-reopen-clicked-branch-overlap-visible.png` — also reconfirms the already-known commit-graph
  branch-pill/author-text overlap bug, still present), then navigated to the Coordinator panel
  (`158-no-coordinator-despite-live-role-coordinator-agent.png`).
- The panel reads **"No coordinator running"** — but `~/.mainguard/logs/rpc.log` shows the daemon's own
  `ListAgents` RPC continuously returning `AgentInfo { agent_id=f1574a0ba9b443ffa2a5b2f9345df622,
  agent_kind=claude-code, state=Paused, role=coordinator }` for this exact repo, once a minute, for the
  entire 20+ minutes surrounding this check — the daemon has never stopped believing this agent is the
  live coordinator. The header line above it independently confirms the rail *did* rehydrate correctly on
  this same launch (`7 in play · 7 in history (merged/rejected, below)`, the `c1e4c3e` fix rendering right
  on a genuinely fresh process) — so this is not a blanket "nothing rehydrates after restart" failure,
  it's specific to the Coordinator terminal-panel's own binding.
- `DaemonBackedOrchestrator.CoordinatorAgentId`'s doc comment claims exactly the reconnect-safe behavior
  that should make this work ("The coordinator is whichever live session carries the role
  (reconnect-safe); a snapshot without one clears it") and the code backing it
  (`ApplyAgentEvent`'s `Snapshot` case) does scan `_agents.Values` for `Role ==
  AgentRoles.Coordinator` on every snapshot — so the mechanism *looks* correct by inspection. Not
  root-caused past that: did not trace whether the `StreamAgentEvents` snapshot this fresh connection
  received actually included this agent (vs. `ListAgents`, which is polled by a different subsystem —
  `LoginHarvestPumpAsync` — and could easily disagree with what the event stream sent), and did not check
  whether the panel additionally excludes a `Paused`-state agent from being treated as "the" coordinator
  even when its role matches (see #20 below for why this agent reads `Paused` at all).
- **Not fixed this leg** — flagged for a dedicated pass. This is matrix row **I1 (restart resume)**, still
  gapped, and is exactly the shape of bug (`CoordinatorAgentId`/snapshot rehydration path) that the #11
  investigation turned out to live in — worth the same depth of tracing rather than a guess.

### FIXED — commit `67b9cc1`. The panel's binding was innocent; the projection under it had lost a field

- **The suspicions above were both wrong, and usefully so.** `CoordinatorAgentId` is not what gates the
  panel — `ShowCoordinatorTerminal` is derived from `_agents.ListAgents().Where(a => a.Role ==
  "coordinator")`, and `CoordinatorAgentId` only backstops the just-spawned-not-yet-projected window.
  There is also **no `Paused` exclusion**: `IsTerminalState` is `Merged|Rejected|Dead|TornDown` only, and
  `MapState("Paused")` parses cleanly. The panel goes to its empty card for exactly one reason — the
  client's projection holds **zero coordinator-role records**.
- **Why it held none.** The projection is fed only by `StreamAgentEvents`: one destructive snapshot at
  subscribe time, then deltas — and **a delta carries neither kind nor role**. `ApplyAgentEvent`'s
  `State` branch therefore fabricates a **role-less placeholder** for any agent it has not seen, and the
  one `ListAgents` call meant to repair it (`ResyncAgentsAsync`) `return`ed silently on a single failure
  and was never retried by anything. A live coordinator stranded that way is invisible to a panel
  filtering on `Role == "coordinator"` **forever**, while the worker rail shows the same agent happily and
  the daemon keeps answering correctly. That is the whole discrepancy — and it explains why the rail
  rehydrated on the same launch: the merge queue is a per-repo, **disk-backed** `StreamQueue`
  re-subscribed at repo-open, whereas the agent snapshot is process-memory truth taken once at app start.
- **The `rpc.log` evidence was the fix.** Those once-a-minute `ListAgents` calls returning
  `role=coordinator` came from the client's OWN login-harvest sweep — which kept only the agent ids and
  threw the rest away. It now folds the answer into the projection first, so a stranded coordinator is a
  coordinator again within the sweep interval at **no extra RPC cost**, off the very call that was
  demonstrably correct throughout the outage. A snapshot is additionally confirmed against `ListAgents`
  rather than trusted (it is a positional `id:kind:state:role` string split on `,`/`:`, not a second
  opinion). The merge corrects **role and kind only** — state and `Detail` flow on deltas, which are
  newer than any poll — and it never deletes.
- **The other leg closes with #18.** If the daemon really has restarted, its session store is empty and
  no amount of client-side repair invents a coordinator; `AgentSessionReconciler` re-registering the
  surviving jail (with its `mainguard.agent.role` label) is what makes the snapshot carry one at all.
  Matrix row **I1 (restart resume)** needs both halves and now has them.
- **Pinned by** `Mainguard.Tests/CoordinatorProjectionRepairTests` (5 cases, through the new
  `AgentListOverride` seam). **Not re-verified through live UI clicks:** the running `Mainguard.app`
  bundle predates this commit, and the one surviving jail on this machine has no
  `mainguard.agent.role` label (it was created before the label existed), so it correctly adopts as a
  role-less worker and could not exercise the coordinator binding either way. Worth one live re-click on
  the next leg after a bundle rebuild, on a freshly spawned coordinator.

## 20. [CLOSED — commit `67b9cc1`] The daemon's tracked agent state can drift from Docker's actual state and nothing brings it back in sync

- The `state=Paused` reading in #19's `ListAgents` output is not a fresh kill-switch pause — `docker
  inspect mainguard-f467e1708a75-f1574a0ba9b443ffa2a5b2f9345df622` reports `Paused=false Status=running`
  right now, confirmed twice. This exact agent was the one kill-switch-paused during the previous leg's
  #17 repro (**before** today's real fix, `ee9be50`, existed) and then manually recovered via a raw
  `docker unpause` as out-of-band cleanup — which by construction bypasses any app-mediated RPC that would
  tell the daemon the physical state changed. `docker events --since 6h` for this container shows no
  pause/unpause events at all in that window, consistent with the pause having happened slightly earlier
  and the unpause being an external command Docker doesn't surface the same way through the events API.
- The result: the daemon has been reporting this agent as `Paused` for 20+ minutes after it was actually
  made to run again. This is expected in the narrow sense that no software can see a change nobody told it
  about — but it exposes a real, reasonable-to-hit gap: **there is no reconciliation between the daemon's
  tracked per-agent state and Docker's live state**, ever, for any reason (a developer's manual
  intervention, OrbStack/Docker Desktop itself restarting, or any other out-of-band drift). Once tracked
  state and reality disagree, nothing — not a poll, not a next-touch check — brings them back together;
  the stale reading can persist indefinitely and, per #19, may be exactly what makes the Coordinator panel
  refuse to reattach to a perfectly healthy live jail.
- Did not attempt a live click-through of the *new* `ee9be50` Resume path against this specific stuck
  agent to see whether pressing Resume today would self-heal it (would have proven whether the fix's
  causation ledger, or a plain `IsPausedAsync` probe, is enough to correct drift it didn't itself cause) —
  sidebar navigation and merge-queue-panel scroll clicks stopped registering visible changes partway
  through this leg (`159-nav-click-did-not-register.png`; the window ID and bounds were confirmed
  unchanged via a fresh `CGWindowListCopyWindowInfo` probe, so this wasn't a stale-window-reference issue —
  cause not diagnosed, flagged as its own small methodology note rather than a product bug) and budget
  ran out before recalibrating. Worth a look bundled with #18 and #19 — all three are "daemon state vs.
  live Docker/process reality can silently diverge, with no reconciliation path" in different clothes.

### FIXED — commit `67b9cc1`

- `AgentSession.State` was **push-only**: something called `MarkState`, or the word never changed. There
  was no poller, no next-touch check, nothing. The same
  `Mainguard.Server/Runtime/AgentSessionReconciler.cs` that closes #18 re-reads Docker every 30 s and
  moves the word, so drift a human (or the engine, or the OOM killer) caused self-heals within half a
  minute instead of standing indefinitely.
- **Only the pause axis is corrected**, deliberately. A live session's state word carries orchestration
  meaning the container cannot know — `RateLimited`, `Yielding`, `AwaitingReview` — and flattening all of
  it to `Working` because the process tree happens to be scheduled would destroy more information than
  the drift did. Docker-says-paused/store-says-not and the reverse are the only two transitions this pass
  makes to a live session.
- **The failure mode that would have made this worse than the bug** is guarded explicitly: the container
  lister is allowed to THROW, and a throwing pass changes nothing. An empty list from a Docker that is
  merely slow to answer would otherwise read as "every jail vanished" and mark the whole swarm lost.
  `Reconcile_ShouldChangeNothing_WhenTheContainerEngineCannotBeReached` pins it.
- **Verified live against the exact agent from this entry.** With a production daemon from this commit
  running and the jail adopted, `docker pause` was run directly from a shell — bypassing every RPC, the
  same way the original drift was created — and the next pass logged
  `agent-session reconcile: adopted=none corrected=f1574a0ba9b443ffa2a5b2f9345df622 lost=none`. A raw
  `docker unpause` (the #20 repro itself) produced the same correction in the other direction, with
  `docker inspect` confirming `Status=running Paused=false`. Also covered end to end by
  `AgentSessionReconcileDockerTests.Reconcile_ShouldFollowAnOutOfBandPauseAndUnpause`.

## 21. [CLOSED — commit TBD] Coordinator terminal renders blank after restart-resume, even once the panel correctly binds to the live agent (#19's own remaining verification step)

- **Repro** (real UI, not RPC): rebuilt the daemon+app from `804ae632` (the #18/#19/#20 fix), started a
  real `claude-code` coordinator through the UI (authenticated, banner visible), then killed and
  relaunched just the app process (daemon left running, jail untouched) — the exact "one live click of
  Engage→Resume-shaped" restart re-test #19's own log entry asked for, applied here to the coordinator
  terminal instead.
- **The panel binding itself is correct** (#19's fix holds): "Stop coordinator" is enabled, the agent id
  is shown, no "No coordinator running" false state. But the terminal pane under it renders **fully
  blank** — just a cursor, no banner, no output — even though it is bound to a live, correctly-identified
  agent. Screenshot `164-terminal-blank-despite-correct-binding-new-bug.png`.
- **Confirmed NOT a daemon/data problem**: a raw `AttachTerminal` RPC against the same agent id, made
  independently via a headless script, returned 2570 bytes of real scrollback (the full claude-code
  welcome banner + auto-mode status line) instantly. The daemon replays scrollback correctly; the UI
  simply never rendered it. A click inside the terminal pane did not force a repaint either
  (`159`→ wait, see `007`/no-op check) — ruled out as a stale-paint issue, not a data-arrival timing
  fluke on the daemon side.
- **Root cause**: `Mainguard.Agents.UI/ViewModels/TerminalViewModel.cs` — `EnsureCoordinatorTerminal`
  (`ControlCenterViewModel.cs:656`) constructs the `TerminalViewModel` and immediately fires
  `AttachTerminalAsync`, which opens the gRPC stream and starts pumping `OnOutputReceived` almost
  instantly for a rehydrated agent (the daemon replays existing scrollback with no CLI-startup delay to
  wait through). But `TerminalViewModel.AttachView(ITerminalView view)` — which sets the `_view` field
  that `OnOutputReceived` feeds — is only called later, when Avalonia's `DataContextChanged` binding pass
  runs in `TerminalView.axaml.cs`'s `Bind()`. On a **fresh spawn**, the CLI's own multi-second startup
  delay before it prints anything covers this race completely, so the bug was invisible every time this
  walkthrough tested a fresh spawn. On **restart-resume**, the replay burst can — and, reproduced here,
  does — arrive before `Bind()` runs, and `_view?.FeedOutput(data)` on a null `_view` silently drops it
  with no error, log line, or visible symptom besides the blank pane.

### FIXED — commit TBD

- `TerminalViewModel` now buffers output (`List<byte[]>` under a dedicated lock) whenever it's called
  with `_view` still null, instead of dropping it. `AttachView` flushes anything buffered into the
  now-real view, in order, the moment it's called — closing the exact race above without changing the
  fresh-spawn path's behavior at all (the buffer is simply always empty there, since `Bind()` beats the
  CLI's first byte by seconds).
- Regression test `TerminalViewModelTests.OutputReceived_BeforeAttachView_ShouldBeBufferedThenFlushed`
  reproduces the race directly (push output, then attach) and **was confirmed to fail against the
  pre-fix code** (`Assert.Single() Failure: The collection was empty`) before the fix, green after.
  A second test, `OutputReceived_AfterAttachView_ShouldStillFeedDirectly_NotDoubled`, guards against the
  fix accidentally double-feeding output that arrives after a normal attach. All 13
  `TerminalViewModelTests` green; `dotnet build` clean.
- **Verified live**, not just by test: killed and relaunched the app again against the same live
  coordinator jail with the fix built in — the terminal pane now renders real content instead of a blank
  cursor. Screenshot `165-terminal-buffering-fix-content-renders-but-garbled.png`.

## 22. [OPEN — needs a dedicated pass, likely Opus] Replayed scrollback renders with corrupted line-wrapping once #21's fix makes it visible at all

- Fixing #21 above makes the replay actually reach the screen — but what reaches it is visibly garbled:
  words split mid-token onto the next line (`"yourl" / "imit,"` for what should read `"your limit,"`),
  fragments duplicated/interleaved (`"o| tonuFable"` where the original banner reads `"o Run /model and
  select"`). Screenshot `165-terminal-buffering-fix-content-renders-but-garbled.png` next to the
  original raw-RPC capture (`checkterm.fsx` output, same session) confirms the RAW bytes the daemon sent
  are correct and un-corrupted — the corruption is introduced during rendering, not transport.
- **This matches an already-logged, older finding** (findings.md, "UI bug — ghost coordinator resurfaces
  with garbled terminal output") — this is very likely the same underlying defect, now reproduced through
  a cleaner, more deliberate repro path (a real restart-resume on a known-good agent, not an incidental
  ghost-coordinator fallback).
- **Leading hypothesis, not yet implemented or verified**: a column-width mismatch. `AttachView`'s flush
  (from #21's fix) happens as soon as Avalonia's `DataContextChanged` binding pass runs — but the
  terminal engine's actual (cols, rows) size is set later, by a **layout-driven** resize
  (`TerminalView.axaml.cs`'s `OnUserResized` → `TerminalViewModel.OnUserResize` → `_view.Resize(...)`),
  which cannot fire until the control has real on-screen bounds. If the buffered replay — recorded by
  the original session at whatever its real terminal width was (visibly 100+ columns wide in the raw
  capture) — gets fed into a `VtScreen` still at its constructor-default size, every absolute
  cursor-position escape in that recording would misplace text relative to the narrower buffer, which is
  exactly the shape of corruption observed (words wrapping early, fragments landing in the wrong place).
- **Why this wasn't attempted in this pass**: fixing it correctly means changing when the buffered
  replay is flushed relative to layout-driven resize (e.g. defer the flush until after the first real
  resize, or resize the screen to a known-correct size before flushing) — timing-sensitive UI-layout
  work against an "interim" terminal engine (`VtScreen`/`TerminalControl`, explicitly flagged elsewhere
  in this repo as due for a P2-18 replacement) where getting the ordering wrong risks reintroducing #21's
  blank-pane bug or breaking live keystrokes on the fresh-spawn path that today works correctly. Per this
  leg's model-tier instruction, flagging for a dedicated Opus/Fable pass rather than attempting it here.
- **Concrete next step for that pass**: confirm the column-width hypothesis (log the `VtScreen`'s
  cols/rows at flush time vs. at the first real resize; or diff against the raw `checkterm.fsx`-style
  capture's implied width from its cursor-position escapes), then decide between "defer flush until
  after first resize" vs. "resize to a sane default before flush, then re-resize" — the former is
  probably correct but needs verifying it doesn't reintroduce a blank-pane window of its own.
