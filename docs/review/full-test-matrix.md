# Full test matrix — everything there is to go over

**Audience: a tester (human or agent) doing an exhaustive pass.** Where `agent-cycle-runbook.md`
proves the merge-critical spine, this covers **every surface, every state, every gate, and the
failure paths** — including the things the on-device macOS run did NOT reach. Each row names *what to
test*, *the states to force*, *the exact assertion*, and *the adversarial move* (try to break it),
plus which automated suite already covers part of it so you know what is belt-and-suspenders vs
genuinely manual.

Legend: **[UI]** must be exercised by clicking; **[RPC]** drivable via the `DaemonBackedOrchestrator`
harness (see the cycle runbook §3); **[auto]** already has a test suite — re-run it, then probe the
gaps it leaves. Run `dotnet test Mainguard.slnx -c Release` (the whole suite) and the opt-in
`MAINGUARD_VERIFY_E2E=1` leg first; this doc is for what those cannot assert.

---

## A. Provisioning & repo binding

- **A1 — cold clone beats the deadline.** [UI] Open a *large* repo (not a 2-file fixture). Assert the
  provision completes (sync remote registered, queue rail appears) — the old 5-second budget failed
  here silently. Adversarial: kill the daemon mid-provision → assert the failure surfaces as a **toast
  + retry card**, not a silent nothing, and Retry recovers. *(auto: `RepoProvisioningHonestyTests`.)*
- **A2 — repo switch clears the queue.** [UI] Open repo A (with a live queue), then open repo B whose
  provision fails/stalls. Assert the rail is EMPTY (not showing A's entries) and a Merge cannot touch
  A's checkout. *(auto: `RepoProvisioningHonestyTests.FailedProvisionOnSwitch…`.)*
- **A3 — shared channel.** Confirm provisioning no longer opens a throwaway `DaemonClient` per call
  (it routes through the adapter's channel). Grep for regressions; watch daemon `rpc.log` for
  per-open channel churn.

## B. Spawn, auth, and identity

- **B1 — BYOK key path.** [UI] Store an Anthropic API key (Settings → AI Providers), spawn a
  claude-code agent. Assert the real key never enters the jail (`docker exec <jail> env | grep -i
  anthropic` shows the gateway `mg_sess_…` token + `ANTHROPIC_BASE_URL` at the daemon gateway, NOT the
  raw key). Adversarial: try to read the key from inside the jail — it must not be there.
- **B2 — interactive CLI OAuth.** [UI] With no key stored, spawn claude-code; assert the start surface
  says "the CLI will ask you to sign in in its terminal" (the honesty hint), then complete the login
  in the PTY and assert it is harvested to `<data-root>/Keyring/cli_login_claude-code.*` and reused on
  the next spawn (no second prompt).
- **B3 — the CLI-OAuth toggle gates nothing.** [UI] Toggle "Use your Claude subscription" and confirm
  it only records the ToS ack + badge (the copy now says so) — it must not change spawn behavior.
- **B4 — role integrity.** [RPC/auto] A coordinator-token client is denied every merge-power RPC:
  BeginMerge/ConfirmMerge/AbandonMerge/AcknowledgeFlaggedChange/DiscardEntry/**RejectEntry**/
  ClearStalledVerification/ResumeAgent/**PauseAgent**/**UnpauseAgent**, plus GetScrollback. *(auto:
  `RoleInterceptorTests` — re-run and confirm the new Pause/Reject rows are present.)*
- **B5 — no secret in argv/env-file.** [auto] `SecretDeliveryDockerTests` — secrets arrive over
  stdin-only 0400 tmpfs. Verify by inspecting the jail's `/proc/<pid>/cmdline` and environment during
  a spawn.

## C. Work execution & the terminal

- **C1 — PTY round trip.** [UI] Type into the coordinator terminal, see output. Note: **synthetic
  keystrokes do not reach a jailed PTY** — a human at the keyboard, or `SendPromptAsync` [RPC], drives
  it. Assert paste (Ctrl+V / Shift+Insert) is bracketed and CR-normalized.
- **C2 — terminal scroll.** [UI] **Untested-live gap.** Run a program that emits many *newline-scrolled*
  lines (a plain shell loop, NOT a full-screen TUI like claude-code, which repaints in place and keeps
  little scrollback). Wheel up → assert older lines appear and the cursor hides; any keystroke snaps
  back to live. *(auto: `TerminalScrollbackTests` covers the buffer + `ScrollbackLine`; the wheel
  handler itself is only manual.)*
- **C3 — OSC 52 clipboard.** [UI] The in-jail CLI's "copy" (claude-code login `c`) lands on the HOST
  clipboard. Assert paste-back works.
- **C4 — send-prompt into the live PTY.** [RPC/UI] `SendPromptAsync` writes the composer text +
  CR. For a MANAGED worker (phase-2 only — absent in phase-1) the daemon input-lock surfaces as a
  `PermissionDenied` rendered under the composer. *(auto: `SendPromptDeliveryTests` — frame-level.)*
- **C5 — status mapping.** [RPC] Drive an agent through Starting→Working→Paused→TornDown and assert
  the rail/AgentLifecycleState mapping (esp. "Stopped"→TornDown, the ghost-coordinator bug).

## D. Verification & the gates

- **D1 — pass path.** [UI/RPC] `.mainguard/verify` runs in the agent's jail; a passing command →
  `Verified`. *(auto: `VerifyInJailDockerTests`, opt-in.)*
- **D2 — FAIL path.** [UI] **Untested-live gap.** Give an agent a branch whose `node test.js`
  genuinely fails (`process.exit(1)`). Assert the entry returns to **Working** (not silently retried),
  and the reason is legible. Then reproduce the review-doc's three-way trap and confirm they are
  DISTINGUISHABLE: (a) genuinely failing tests, (b) a **mis-tokenized** verify command (e.g. quotes
  wrong), (c) a **missing toolchain** — all three land differently-worded, not all as "tests failed."
- **D3 — changed-test-command gate.** [RPC] A branch that edits `.mainguard/verify` verifies green but
  is BLOCKED until acknowledged; the flagged item names the command; the cockpit header shows the
  warning even without a run-count delta. *(auto: `MergeQueueProvisionerTests`,
  `QueueProjectionRenderingTests`.)* Adversarial: a branch that only adds a comment/blank line to the
  verify config must NOT flag (noise trains reviewers to ack blindly).
- **D4 — changed-toolchain gate.** [RPC] Same, for `.mainguard/toolchain`; the reason must name the
  *toolchain* specifically (a reason saying "test command" sends the reviewer to the wrong file).
- **D5 — flagged-change (P2-11).** [UI] A branch touching a CI workflow / git hook / out-of-approved-
  scope path surfaces flagged items in the cockpit; the merge is blocked until each is acknowledged.
- **D6 — forged verdict is overridden.** [auto] `ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit`
  — a compromised supervisor cannot forge a pass; the daemon-observed container exit wins.
- **D7 — no-verification-command refusal.** [RPC] A repo with no `.mainguard/verify` → verification
  refuses with a typed reason, never a false green.

## E. Merge queue lifecycle — every state and transition

Walk the state machine and try every illegal transition (it must throw, not silently accept):
`Working → Verifying → Verified → (StaleVerified | AwaitingReview | Working) → Merged`, plus terminal
`Rejected`, `Discarded`. *(auto: `MergeQueueStateMachineTests` — the enumerated legal set.)*

- **E1 — Verify trigger.** [UI] The per-row Verify button; `VerifyMessage` renders the daemon's
  refusal verbatim; the button re-arms after a refused run.
- **E2 — Review reachable from EVERY verified entry.** [UI] Two+ verified entries: the front one has
  the accent Review, every OTHER reachable via the secondary Review; each opens the cockpit (the only
  home of the Merge button). *(auto: `QueueProjectionRenderingTests.EveryReviewableRow…`.)*
- **E3 — verified-against stamp.** [UI] A Verified row shows `main@<sha>` (from the wire's
  `VerifiedMainSha`). *(auto: `QueueProjectionRenderingTests`.)*
- **E4 — Discard.** [UI] Discards any non-terminal entry to `Discarded` (leaves the stream); refused
  under an outstanding merge lease; records `queue_entry_discarded` audit + a persisted record.
  *(auto: `QueueEntryLifecycleTests`.)*
- **E5 — Reject.** [UI] The review verdict "no": legal only from Verified/AwaitingReview, terminal
  `Rejected`, stays on the stream (unlike Discard), records `queue_entry_rejected` audit with actor+
  reason; refused for an un-verified entry ("discard the entry instead"); coordinator-denied.
  *(auto: `MergeQueueStateMachineTests` TryReject legs, `QueueEntryLifecycleTests`.)*
- **E6 — Clear stalled verification.** [UI] A `Verifying` row with no live run → back to Working;
  refused while a run is really in flight ("wait").
- **E7 — Resume a stranded entry.** [UI] An entry whose jail is gone (agent stopped) → Resume gives it
  a fresh jail on its own branch; refused if the branch is gone. *(auto: `QueueEntryResumeDockerTests`
  — flaky under load; run isolated.)*

## F. Merge execution & cascade

- **F1 — RT-D1 three-step.** [UI/auto] BeginMerge (lease + `CanMerge` under it) → client `git merge
  --ff-only` on YOUR checkout → ConfirmMerge with the real sha. A merge that lands nothing → the queue
  is untouched. *(auto: `MergeQueueEndToEndDockerTests`.)* Adversarial: kill the app between BeginMerge
  and ConfirmMerge → the boot reconcile must synthesize the confirm from the T-19 journal (exactly
  once, or none).
- **F2 — dirty-tree / not-ff refusals.** [UI] Uncommitted changes in the checkout → merge refuses with
  a phrased reason; a branch that no longer fast-forwards → refused, main NOT moved.
- **F3 — post-merge mirror refresh.** [RPC] After a confirmed merge, `git --git-dir <mirror> rev-parse
  main` advances; a spawn in that window does NOT walk the queue's main backwards. *(auto:
  `MergeQueueProvisionerTests.RefreshMirrorMainAfterMerge…`.)*
- **F4 — stale cascade.** [UI] Merge one of N verified co-tenants → the rest go StaleVerified → keep-
  alive rebase → re-verify against the new main. *(auto: `MergeQueueEndToEndDockerTests.WhenOneBranch
  Merges…`.)* Adversarial: a co-tenant whose rebase CONFLICTS → it blocks with the jail paused for a
  human, not a silent skip.
- **F5 — external PR entry.** [auto] An intake'd upstream PR merges on its HOST, the checkout
  converges on that merge (never a local ff behind the PR's back). *(`ExternalPullRequestEntry…`.)*
- **F6 — Bring local.** [UI] Fetches the agent branch into the checkout; refuses a diverged/checked-out
  local branch with git's stderr (never rewrites local work); HEAD does not move. *(auto:
  `BringLocalServiceTests`.)*

## G. Pause / kill / teardown

- **G1 — human Pause/Resume.** [UI] Right-click an agent in Resources → Pause; `docker inspect`
  Paused=true; Resume → false. **The macOS run triggered these by RPC, not the menu — click the menu
  here.** *(auto: `AgentPauseTests` incl. a real docker leg.)*
- **G2 — pause vs cascade arbitration.** [auto] A human pause survives a keep-alive yield (never
  woken); a human unpause defers to an in-flight machine hold. *(`YieldProtocolTests` arbiter legs.)*
- **G3 — kill switch.** [UI] **Completely untested live.** "Stop all" freezes EVERY jail (inspect all,
  not just the first), freezes the queue (BeginMerge/DiscardEntry refused), and a partial-pause
  failure marks the uncontained jail "Unresponsive" (never a false "Paused"). Resume restores.
  *(auto: `KillSwitch_FreezesQueueBeforeFanOut`, SA-1 legs.)* Adversarial: engage it mid-verification
  and mid-merge.
- **G4 — Stop coordinator / End task.** [UI] **Untested live** — the run killed the app instead. Assert
  teardown removes the jail, the IPC endpoint, the PTY, and harvests the login; the queue entry is
  left (stranded) until Discard/Resume, so nothing is silently lost.

## H. The other surfaces

- **H1 — Resource monitor honesty.** [UI] Live CPU/RAM per agent; an *unmeasured* reading renders "—"
  while a measured zero renders "0%"; the spend column shows the unmetered explainer (not `$0.00`)
  for interactively-logged-in agents. *(auto: `ResourceMonitorHonestyTests`.)*
- **H2 — Egress.** [UI] Open Network…/egress allowlist; an agent that hits a non-allowlisted host is
  BLOCKED and audited, and raises the unblock prompt; adding the host live re-renders the proxy.
  *(auto: `DaemonGitProxyDockerTests`, `EgressAllowlistReachabilityTests`.)* Adversarial: `curl
  pastebin.com` from inside a jail → blocked + a Sandbox-health event.
- **H3 — Review cockpit depth.** [UI] Flagged items, acknowledgments, the changed-command header, the
  verified-@ stamp, provenance chips where present. Coordinator entries titled "Coordinator (<cli>)".
- **H4 — Daemon logs page.** [UI] Tools → Daemon Logs renders the per-subsystem tails (macOS reads the
  host rolling files; WSL reads journalctl). Non-empty after a spawn.
- **H5 — OOBE.** [UI] First run on a clean data root: engine detection (names the engine + version),
  file-sharing canary, image build/probe, daemon start, CLI picker, start-at-login. Every failed check
  is a named, retryable row.
- **H6 — Deep links (macOS).** `mainguard://agent/<id>` activates the app and jumps to that agent;
  never carries a secret in the URL.
- **H7 — Themes / design.** [auto] `ThemeContrastGateTests` (AA, deuteranopia, terminal legibility)
  and the render harnesses (~436 PNGs across the 4 themes). Eyeball the coordinator/review/terminal
  captures in `artifacts_headless/`. On macOS, toggle the vibrancy preference and confirm the opaque
  fallback is the harness look.

## I. Daemon lifecycle & security invariants

- **I1 — restart resume.** [UI] Quit (not Stop-all) → relaunch → re-attaches to running agents;
  interrupted `Verifying` resumes; a queue whose main advanced while dead re-fires the cascade on boot.
- **I2 — daemon auto-update.** On launch a skewed daemon is refreshed from the payload; assert the new
  version answers and the queue rebuilds.
- **I3 — audit-per-authority-action.** [auto] Exactly one audit event per touchpoint. Note: the audit
  log is **in-memory today** (P2-15 not built) — so "tamper-evident" is NOT yet true (RT-D3 is only
  nominally green). Flag this; it is the top backlog item.
- **I4 — ESC §4 conformance.** [auto] `SubstrateConformanceTests` + the cited suites: git-objects
  round-trip byte-identical, no host-path mount, quarantine-only remotes, hardened-spec flags,
  secret-channels-only. Rows 4/5 (teardown-no-residue, health/upgrade idempotence) are OPEN.
- **I5 — no auto-merge path.** [auto] A reflection proof that no code path merges without the human
  three-step; `NoAutoMergePathExists`.

---

## J. Known flakes & gotchas

- `RequiresDocker` tests (`Fake429EndpointTests`, `QueueEntryResumeDockerTests`) flake under OrbStack/
  Docker load when jails accumulate — `docker rm -f` the orphan jails, re-run isolated.
- The whole-suite `dotnet test` is heavy; use `--filter` locally, let CI run the full set. Never build
  in `/tmp` (tmpfs = RAM).
- Synthetic keystrokes/clicks: OS-specific and brittle. Prefer the RPC harness for assertions; use the
  UI for the things that ONLY exist as clicks (Review/Merge/Reject/Pause-menu/kill switch/OOBE).
- macOS GUI automation needs Accessibility granted to the terminal; capture-pixel→screen-point mapping
  drifts with window focus (shadow margin differs) — recalibrate per window with a cursor-visible
  `screencapture -x -C`.
