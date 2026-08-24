# Live UI walkthrough — Windows/WSL2, 2026-08-24

Reproduces the macOS 2026-08-20 walkthrough's methodology (`docs/review/walkthrough-2026-08-20/`)
on this Windows/WSL2 machine, per `docs/review/agent-cycle-runbook.md`. Substrate: daemon runs
inside the `MainguardEnv` WSL2 VM (`Wsl2AgentEnvironment`), Docker via `wsl -d MainguardEnv --
docker`, sync remote `mainguard-vm`, data root `%LOCALAPPDATA%\Mainguard\`.

No "part 2" fix list was actually included in the task brief — the FIXED/CLOSED entries in the
Mac run's `ISSUES-LOG.md` are used as the re-verification list instead.

## 0. Environment & GUI-automation calibration

- Checked out `port/macos` (was on `phase2`; the runbook and matrix docs only exist on
  `port/macos`), pulled — already up to date with `origin/port/macos` at `6124b3b`.
- Windows/WSL2 has no CGEvent/AppleScript equivalent. Calibrated a PowerShell-based harness:
  - Screenshot: `System.Drawing.Graphics.CopyFromScreen` over the full virtual screen.
  - **Calibration finding:** cropping a screenshot to a window's `GetWindowRect` bounds does NOT
    match the actually-rendered window — Avalonia's window rect includes an invisible margin
    substantially larger than typical OS resize borders, so a rect-cropped image bled in desktop
    content from outside the app on all edges (see the discarded `001-app-launch.png` first
    attempt). Fix: maximize the window (`ShowWindow(SW_MAXIMIZE)`) before every screenshot and
    capture the full virtual screen — no cropping needed, no bleed.
  - **Calibration finding:** raw synthetic clicks via `mouse_event` at correct, verified
    coordinates (confirmed via `Cursor.Position` and a UIA `BoundingRectangle` cross-check) were
    silently ignored by the app — the click never reached the Avalonia window. Root cause not
    fully chased (candidates: UIPI, session/window-station quirk of a WSL-interop-launched
    process, or Avalonia's Win32 backend requiring `SendInput` specifically). **Fix:** all clicks
    go through `SendInput` (absolute-coordinate mouse input), and buttons/menu items are clicked
    via **UI Automation `InvokePattern`** where an element is exposed (Avalonia exposes named UIA
    elements for most interactive controls — confirmed by enumerating the live tree), falling back
    to a `SendInput` click at the element's `BoundingRectangle` center when no `InvokePattern` is
    supported. Verified working end-to-end: invoked the "Reopen Last Repository?" dialog's
    **Dismiss** button (stale reference to an old `mg-testrepo` fixture from an earlier, unrelated
    session) via `InvokePattern` and confirmed it cleared.
  - Toolkit lives at (scratchpad) `mg-automation.ps1`, mirrored to
    `C:\Users\yikes\AppData\Local\Temp\mg-automation.ps1` for PowerShell to source.

## 1. Substrate prerequisites (runbook §1)

- `dotnet build Mainguard.slnx -c Release` — succeeded (WSL2/Linux-side dotnet, `~/.dotnet`, SDK
  `10.0.301`), 0 errors, 7 pre-existing warnings unrelated to this pass. `packages.lock.json`
  unchanged (checked per the WSL lockfile-drift gotcha).
- `wsl -d MainguardEnv -- docker info` — reachable, server 20.10.24+dfsg1, confirms
  `MainguardEnv` auto-starts on invocation despite showing "Stopped" in `wsl -l -v` beforehand.
- Jail images present: `mainguard-agent-base`, `mainguard-agent-toolchain`, `mainguard-egress-proxy`.
- **Clean slate:** found 12 leftover exited jails from prior sessions (some 4 weeks old) —
  removed via `docker rm -f`.
- Built `Mainguard.Pro.App` a second time with the **Windows-side** dotnet SDK (`10.0.302`,
  same `10.0.3xx` band) — the Linux-side build produces linux native deps (SkiaSharp/ANGLE) that
  won't run as a real Windows GUI process; the actual client head must be built with the
  Windows-side toolchain to get a real window on the Windows desktop.
- Launched `Mainguard.Pro.App.exe` directly (`Start-Process`, detached). Confirmed the daemon
  (`mainguardd`, PID 470) started **inside** `MainguardEnv`, with a fresh `daemon.token` +
  certs generated at launch time, and the client-side data root
  (`C:\Users\yikes\AppData\Local\Mainguard\`) populated (`daemon.token`, `config.json`,
  `Keyring\`, `agent-ipc\`, `vm\`, plus older pre-rename `gitloom.db`/`gitloom-daemon.db` files
  from before the rebrand — not touched, out of scope).

## 2. Fixture repo + scripted agent (runbook §2)

- An old fixture (`C:\Users\yikes\Code\mg-testrepo`, Python/pytest-based, `master` branch, from an
  unrelated Aug 6-9 session) already existed and was offered by the app's "Reopen Last
  Repository?" prompt — dismissed rather than reused, since it doesn't match the runbook's exact
  node/`.mainguard/verify` contract.
- Created a fresh fixture at `C:\Users\yikes\Code\e2e-fixture` exactly per runbook §2a (node
  `calc.js`/`test.js`, `.mainguard/verify` = `node test.js`, `main` branch, seed commit
  `769e21b`).
- Wrote `scripted-agent` and `scripted-evil` into
  `/home/mainguard/mainguard/adapters/{bin,registry}/` inside `MainguardEnv` (writing via heredoc
  through `wsl.exe -d MainguardEnv -u mainguard -- bash -lc '...'` mangled shell-metacharacter
  quoting across the WSL/bash/heredoc boundary — worked around by base64-encoding the file
  contents in the outer (Linux) shell and decoding inside the VM, which sidesteps nested-quoting
  entirely). Verified byte-for-byte correct content after write.


## 3. RPC driver harness (runbook §3)

Wrote `rpc-harness.fsx`, referencing DLLs from `Mainguard.Server.Tests/bin/Release/net10.0/`
(built with the **Windows-side** dotnet, since the harness must reach the daemon over
`127.0.0.1:5250` exactly as the Windows client does) plus
`Microsoft.Extensions.Logging.Abstractions` 9.0.0/net8.0 from the NuGet cache, per the runbook.
`dotnet fsi` run via `powershell.exe` (not WSL bash — a Linux-side fsi would sit in a different
network namespace than `MainguardEnv`'s forwarded loopback).

First run: connected, got `GetDaemonInfoAsync` (daemon `0.2.8+6124b3b5e…` — matches this
branch's HEAD, confirming the daemon really is running the code under test), `ProvisionRepoAsync`
on the fixture — sync remote name came back **`mainguard-vm`**, exactly as the substrate table
predicts (vs `mainguard-local` on macOS). Registered the git remote on the checkout (its URL is a
`\\wsl.localhost\MainguardEnv\...` UNC path — added via WSL git since it just writes the config
string, but any actual fetch/merge against it must go through **Windows** git, since WSL git does
not understand UNC paths).

### Finding: sandbox image runtime rebuild is broken on this Docker engine (TARGETARCH empty)

`SpawnAgentAsync(role="coordinator")` initially refused with `FailedPrecondition`: "Mainguard OS
sandbox image(s) need provisioning (outdated: mainguard-agent-base:latest)". The daemon's own log
(`~/.mainguard/logs/lifecycle.log` on the Windows client) showed it attempting a **runtime**
`docker build` of `mainguard-agent-base` (triggered because this branch's newer commits moved the
`SandboxImageVersions.AgentBase` source-hash constant past what the existing 2-week-old image was
labeled with) — and that build failing:

```
sandbox images: mainguard-agent-base:latest — BuildFailed: docker build exited 1: The command
'/bin/sh -c case "${TARGETARCH}" in amd64) ...; arm64) ...; *) echo "no pinned nix-installer for
TARGETARCH ''" >&2; exit 1 ;; esac && curl ... nix-installer-${NIX_PLATFORM} ...' returned a
non-zero code: 1
```

**Root-caused, not guessed:** reproduced the exact `docker build` by hand from the payload's
`images/mainguard-agent-base/Dockerfile` — `TARGETARCH` came back **completely empty**, hitting
the Dockerfile's `*)` fallback. `TARGETARCH` is a BuildKit-automatic build-arg that this machine's
Docker engine (`20.10.24+dfsg1` inside `MainguardEnv`, an older engine without BuildKit's
platform-inference active) never populates for a plain `docker build` — unlike the Mac run's
OrbStack/Docker Desktop, which auto-populates it. Confirmed the network/pin itself is completely
fine: manually curling the pinned Nix installer URL
(`https://install.determinate.systems/nix/tag/v3.21.8/nix-installer-x86_64-linux`) inside an
identical `debian:bookworm-slim` container returns HTTP 200 with a SHA-256 that matches the
Dockerfile's pin exactly. This is a genuine substrate difference in the "Docker build backend"
category the brief called out as a risk area, not literally named there but the same species of
bug (shared code assumes a modern container-engine default that silently doesn't hold on older
WSL2/Docker Desktop configurations).

The Dockerfile's own header comment states this image must be **"Built in CI / the release
pipeline ONLY — NEVER at runtime (G-16: a runtime docker build severs the agent PTY)"** — yet the
app/daemon startup path (`SandboxImageProvisioner` / the daemon spawn preflight) DOES attempt
exactly that runtime rebuild whenever it detects the image is stale, and that path is what's
broken here. Whether the *design* (falling back to a live rebuild at all when G-16 says never) or
just the *TARGETARCH plumbing* is the bug is a genuine open question — flagged for the fix, not
decided unilaterally mid-walkthrough.

**Unblocked immediately (per the standing instruction) with a manual, minimal, non-code
workaround**: rebuilt the image by hand with the missing pieces the runtime path should have
supplied — `docker build --build-arg TARGETARCH=amd64 --label
mainguard.image.version=<SandboxImageVersions.AgentBase> -t mainguard-agent-base:latest .` — which
built clean and let the daemon's preflight accept the image immediately. This is a docker-side
fix only; **no source file was changed for this**, and the real code path that constructs the
runtime `docker build` invocation still needs a proper fix (tracked as an open finding below, not
applied yet pending confirming the exact call site).

### Cycle steps 1-2 (spawn + verify) — PASS

With the image fixed, re-ran the harness clean:
```
== spawning scripted coordinator ==
agentId=0372e37675dc497490172d00cb90fe8c
== waiting for agent branch to appear (up to 20s) ==
for-each-ref output: 82cc22e30f726743d4864bd1ad272058ed7c685d commit refs/heads/agent/0372e37675dc497490172d00cb90fe8c
branch found: true (after 1 s)
== running verification ==
Ran=true Passed=true Reason=verified against main@769e21bd
queue entry: agent=0372e37675dc497490172d00cb90fe8c state=Verified verifiedMainSha=769e21bdad4286eb79a88edbdd4ab3ec9b6a706b hasLiveSandbox=true
CanMerge=true reason=
```
Matches runbook §4 step 1's assertion (jail + `agent/<id>` ref within ~20s — here 1s) and step 2's
assertion (`Ran=true, Passed=true`, `Verified` state, `VerifiedMainSha` set, `CanMerge=true`).

## 4. GUI automation limitation found: repository-list rows are not activatable via UI Automation or synthetic mouse input

While attempting to open the fixture repo through the app's own "Select Repo" picker (a secondary
`RepoPickerWindow`), found and root-caused (see ISSUES-LOG) that repository list rows expose only a
bare `TextBlock` to UI Automation with no `InvokePattern`/`SelectionItemPattern` ancestor before the
containing `ListBox`/`ScrollViewer`/`Window` — and that neither raw `SendInput` mouse clicks nor a
genuine two-click double-click (confirmed via `SetCursorPos`/`Cursor.Position` cross-check, so the
click coordinates were verified correct) register with this Avalonia surface at all. UI Automation
`InvokePattern` DID work for named elements that expose it (buttons: Dismiss, the toolbar's folder
icon, "Select Repo" menu item), and `SendMessage(hwnd, BM_CLICK, ...)` worked for the native Win32
"Select folder" common dialog's buttons. Pivoted to the RPC harness for repo provisioning, per the
runbook's own preference for RPC over "OS-specific and brittle" GUI automation for setup steps —
this is exactly that case. Logged as an accessibility/automation gap (ISSUES-LOG), not fixed inline
(would need investigating why Avalonia's `ListBox` items aren't getting per-item automation peers,
a real but non-blocking UI concern separate from this pass's goal).

### Bonus finding: auto-detected repo entry mislabeled as ".git"

Using the picker's "auto-detect repositories" folder-browse flow (invoked via UI Automation
`InvokePattern` on the toolbar icon — this DID work) and pointing it at a folder that IS itself a
git repo (rather than a parent folder containing several repos), the newly detected entry was
added to the list labeled **`.git`** instead of the repo's actual folder name (`e2e-fixture`). Data
is fine (the entry is real and selectable) — purely a display-label bug, most likely the
auto-detect scanner using the found `.git` subdirectory's own name as the display name instead of
its parent when the scan ROOT itself is the repo (as opposed to the common case of scanning a
parent folder containing multiple repos, where the parent's name is correctly used, as seen for
`AGY Cortex`/`demo`/`GitLoom`/etc.). Logged in ISSUES-LOG, not fixed (cosmetic, low severity, not
blocking).

## 5. Cycle steps 3-8 (runbook §4) — via RPC harness, all PASS

### Step 3-4: bring local + merge — PASS
```
== bring branch local ==
Done=true LocalBranch=agent/474ebd2b7c9c44a2b12186b6cf259c4b Reason=
second bring-local: Done=true LocalBranch=agent/474ebd2b7c9c44a2b12186b6cf259c4b Reason=
== merge ==
MergeOutcome: Origin=Local AgentId=474ebd2b7c9c44a2b12186b6cf259c4b MainBranch=main NewMainSha=a68846e7...
second ConfirmMergeAsync correctly refused: Can't merge — already merged.
```
Verified independently: `git rev-parse main` in the checkout now reads `a68846e7...` (matching
`NewMainSha`), `src/note-474ebd2b.js` exists, `node test.js` prints "all tests green". Re-running
`BringBranchLocalAsync` a second time correctly no-ops (`Done=true`, no error) rather than
refetching/erroring.

### Finding W4: git "dubious ownership" blocks every fetch/merge against the WSL UNC mirror remote

The first `BringBranchLocalAsync`/`ConfirmMergeAsync` attempt refused with a git-native error, not
a Mainguard one:
```
fatal: detected dubious ownership in repository at '\\wsl.localhost\MainguardEnv\home\mainguard\...'
```
Confirmed this is **not** an artifact of my manual `git remote add` (skipping something the real
`SyncRemoteRegistrar` does) — read `Mainguard.App.Shell/Services/SyncRemoteRegistrar.cs` in full:
it does exactly a remote add/update via `IGitService`, nothing else, and grepping the whole
`Mainguard.Agents`/`Mainguard.Agents.UI`/`Mainguard.Git` trees for `safe.directory` / `safe\.directory`
/ `dubious` returns **zero hits** — nothing in the codebase ever configures this, on any platform.
So this will reproduce identically for every real user on a fresh Windows+WSL2 install the first
time they try to merge, not just in this harness.

**Attempted the narrow, correct fix first** — `git config --global --add safe.directory
'<exact mirror UNC path>'` — and it did **not** work, even though the stored `.gitconfig` value and
the path git reported as dubious were character-identical on inspection. Diagnosed (not guessed)
that this is a known git-for-Windows quirk with `\\wsl.localhost\...`-style UNC paths and
`safe.directory`'s exact-string matching (likely an internal path-normalization difference between
what git resolves the repository's canonical path to versus the literal string accepted on the
command line) — confirmed by testing `safe.directory=*`, which worked immediately for the exact
same path. Used `*` **only as this test session's manual, local unblock** — it is a real (if
narrow) security relaxation and was not applied as a source change; a real fix needs someone to
either find the exact string git actually wants for a WSL UNC path, or reroute the sync remote
through a mapped drive letter / a path form git's ownership check accepts natively.

**Not fixed this pass** (per the standing guidance — this needs someone to nail down the exact
path-matching behavor, not a one-line guess): logged as a HIGH severity, blocking-by-default
finding in ISSUES-LOG. Every subsequent RPC-harness run in this walkthrough runs with
`safe.directory=*` active on this machine — noted here so that fact travels with the rest of the
day's results.

### Step 5: changed-test-command gate (scripted-evil) — PASS
```
Ran=true Passed=true Reason=verified against main@a68846e7
state=Verified flaggedItems=1
  flagged: FlaggedItem { Id = changed-test-command, ..., Fact = the test command changed on this branch vs main — a branch cannot be allowed to self-green, Acknowledged = False }
CanMerge=false reason=the test command changed vs main — acknowledge to merge
merge correctly refused: Can't merge — the test command changed vs main — acknowledge to merge.
== rejecting the evil entry ==
rejected by=uid:1000 at=24/08/2026 21:15:09 +00:00
post-reject state=Rejected
```
Matches every runbook §4 step-5 assertion: reaches Verified but CanMerge=false, FlaggedItems names
`changed-test-command`, ConfirmMerge throws, Reject transitions it to terminal `Rejected` (not
gone from the stream).

### Finding W5 (minor): rejected-by/discarded-by actor renders as a raw `uid:1000`, not a human identity

On the Mac run, `RejectEntryResponse.rejected_by` rendered as `os:danielsazykin` (ISSUES-LOG #13) —
a real macOS account name. On this machine the same field comes back **`uid:1000`** — the WSL2
VM's internal Linux uid, not anything a Windows user would recognize as themselves. Whatever
resolves "the current actor" (`_identity.Resolve(context)` server-side, per
`MergeQueueGrpcService.cs`) evidently falls back to a bare POSIX uid when it cannot determine a
real OS account name — worth checking whether that's because this RPC harness connects without the
Windows-side identity context the real app supplies, or whether the Windows CLIENT genuinely has no
equivalent of `os:<username>` wired up at all (i.e. every real Windows user would see `uid:1000` in
their own audit trail, not their own name). Not chased further this pass — logged for follow-up;
low severity (correctness is unaffected, only the human-readability of the audit actor field).

### Step 6: stale cascade — PASS
Spawned two honest scripted agents (A, B), verified both, merged A, then polled B every 2s for 30s:
B was back to `Verified` with `VerifiedMainSha` equal to A's merge's `NewMainSha` on the **very
first** poll (≤2s) — the cascade + keep-alive rebase completed faster than this harness's polling
granularity could catch the transient `StaleVerified` state. Confirmed the *outcome* the runbook
cares about (B ends up re-verified against the NEW main, unprompted) — a positive result, just too
fast to also capture the intermediate state on video, so to speak.

### Step 7: pause/resume — PASS (real docker jail, RPC-driven)
```
Paused before: false
Paused after PauseAgentAsync: true
Paused after ResumeAgentAsync: false
```

### Step 8: kill switch — PASS, re-confirms ISSUES-LOG #17's fix holds on WSL2/Docker
Spawned 2 live jails, engaged via `IKillSwitchService.EngageAsync()`:
```
after engage: IsFrozen=true Phase=Frozen PhaseText=queue frozen · 11 agents paused
jail1 Paused=true  jail2 Paused=true (BOTH must be true, not just the first)
== resume ==
after resume: IsFrozen=false Phase=Armed
jail1 Paused=false  jail2 Paused=false (BOTH must now be false)
```
Both jails paused, both correctly un-paused on Resume — this is exactly the bug the Mac run's
ISSUES-LOG #17 fixed (`ee9be50`, "Resume does NOT un-pause a real jail"); it holds under Docker
Desktop's WSL2 backend, not just OrbStack.

**Correction to my own first read, logged so it isn't relitigated:** initially misread a
`DiscardEntryAsync` succeeding *while frozen* as a regression against `full-test-matrix.md`'s G3
line ("freezes the queue (BeginMerge/DiscardEntry refused while frozen)"). Reading
`MergeQueueGrpcService.cs` shows this is **deliberate, documented behavior**, not a bug — the doc
comment directly on `DiscardEntry` states "The kill switch does NOT gate it. Freezing the queue
stops merges; it is not a reason to forbid the human from tidying an entry that can no longer merge
either way," and `RejectEntry`'s comment says the same for review verdicts. `ThrowIfFrozen` is
called by `BeginMerge` and `ConfirmMerge` only — exactly the two operations that actually touch git.
**The matrix's own G3 wording is the stale artifact here, not the code** — flagging the mismatch
(ISSUES-LOG) rather than "fixing" working, intentionally-designed behavior.

## 6. Real UI verification: repo opened, Review cockpit, Merge — all via actual clicks

### Harness correction: screenshots were clipped to ~67% of the window this whole session

Discovered while trying to see the Merge Queue rail (off the right edge in every prior
screenshot): this machine's physical resolution is 2560×1600, but pinning the harness thread to
`DPI_AWARENESS_CONTEXT_UNAWARE` (the earlier calibration fix) captures at the **virtualized**
1707×1067 — correct for click-coordinate consistency, but it silently cropped every screenshot to
the left ~67% of the real window, since the app itself renders at full physical resolution (a
proper per-monitor-DPI-aware Avalonia app). Switched to
`DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2` instead: screenshots now consistently come back
2560×1600 across repeated calls (verified 3x), and — because UI Automation coordinates are
independent of the caller's DPI context — this changes nothing about the click mechanism (still
`InvokePattern`/`BM_CLICK`, never raw `SendInput`, which this session conclusively confirmed
**never reaches this app's input pipeline via any mouse method tried**, not just for list rows: a
literal, coordinate-verified click on the hamburger menu icon — a plain, definitely-clickable
button — produced no effect either, only `InvokePattern` did). No prior finding in this log
needs retracting — every actual interaction was always driven by `InvokePattern`/`BM_CLICK`, never
by the (silently no-op) `SendInput` calls — but every prior *screenshot* undersold how much UI was
actually there. Re-took the Coordinator/Merge-queue screenshot after the fix; the rail was there
all along.

### Unblocking repo-open in the real UI (working around W2)

Since W2 (repository-list rows have no `InvokePattern`) blocks opening a NEW repo through the
picker, and I needed a repo genuinely open in the app's own UI (not just bound via my separate RPC
harness connection) to test Coordinator/Merge Queue/Review panels live: edited
`C:\Users\yikes\AppData\Local\Mainguard\config.json`'s `LastOpenedRepoPath` from the stale
`mg-testrepo` to `e2e-fixture` directly, closed the app (`Stop-Process`, not a graceful Exit — the
menu-bar toggle needed to reach Exit wasn't landing reliably at that moment; a proper graceful-quit
restart-resilience check is still owed separately), relaunched, and used the **"Reopen Last
Repository?"** prompt's real `Reopen` button (confirmed `InvokePattern`-capable) — landing on the
real Repo Viewer for `e2e-fixture` (branch `main`, Sync/Repository menus, staging panel all
present). This is a config-file workaround for the picker bug, not a repo-opening mechanism under
test itself — same spirit as using RPC for setup.

### Coordinator panel — live, real terminal output

Navigating to **Coordinator** (real click) showed a genuine live terminal for one of the scripted
coordinators spawned earlier via RPC, with its actual commit output
(`[agent/ba2a7f91... 7d68c4e] feat: add note module (ba2a7f91)`) — confirms C1 (PTY round trip) is
real, not just RPC-asserted.

### Merge queue rail — re-confirms the Mac run's count-header fix (`c1e4c3e`) holds

The rail showed **"8 in play · 3 in history (merged/rejected, below)"** — the exact header format
`c1e4c3e` introduced (ISSUES-LOG #13/#4) to fix the "stale entries push new ones below the fold
with no cue" bug. Holds correctly under this substrate with a genuinely large (11-entry) queue
built up over this session's testing.

### Review cockpit + Merge — real clicks, both outcomes confirmed

Clicked **Review** (real `InvokePattern` click) on the top queue entry: cockpit opened with the
correct title format `Review — Coordinator (scripted) · agent/<id> → main`, the diff view, and
Reject/Bring local/Merge buttons — matches H3 exactly (title names the CLI kind, not a raw GUID).

Clicked **Merge** on that first entry: `BeginMerge` was granted, then correctly **abandoned** —
"verification is stale — the branch no longer fast-forwards onto 'main'". Traced this to real git
topology, not a bug: this session's many parallel RPC test runs forked ~9 sibling `agent/*`
branches off different points as `main` advanced past each of them independently, and this
particular entry's branch genuinely predates the current `main` tip with no common
fast-forward path. **This is F2's exact contract working correctly** ("a branch that no longer
fast-forwards → refused, main NOT moved") — confirmed `main`'s sha was unchanged after the abandon.

Picked a second entry whose branch sits directly on the current `main` tip (confirmed via
`git log --graph`), clicked **Review** then **Merge** on it for real: `main` advanced to that
agent's own commit sha, and `node test.js` still printed "all tests green" afterward. **F1's full
three-step (BeginMerge → client `git merge --ff-only` → ConfirmMerge) verified via actual UI
clicks**, not just the RPC harness.

## 7. Real UI verification: Resources panel (H1) + kill switch (G3) — full cycle, real clicks

Navigated to **Resources** (real click): showed all 13 live scripted-agent jails, `CPU 0%`/
`RAM 0.0 GB` per row (a genuinely-measured zero, not the unmeasured-`—` case — consistent with
trivial idle `sh -i` jails), and the honest unmetered-spend explainer text verbatim: *"Spend isn't
tracked for these sessions... Agents signed in interactively talk to the provider directly, so
there is no figure to show — not a figure of zero."* Matches H1 exactly. Bonus: 3 rows showed
**"Rebasing onto the new main after a merge"** live in the Task column — the stale-cascade
keep-alive rebase from step 6, visibly legible to the user, not just internally tracked.

**Kill switch (G3), via the real sidebar "Stop all" button** (its child text has no `InvokePattern`
— same family as W2's list-row gap, but its parent IS a real `Button` with `InvokePattern`, so this
one's clickable): invoked it for real.
- Button label correctly flipped to **"Frozen — resume"**.
- `docker ps` (ground truth, not the UI): **all 13 jails** showed `(Paused)` — not just the first.
- Resources panel (after a short live-refresh delay): every row updated to **`Paused`** with the
  exact message *"Kill switch engaged — jail paused, terminal input severed. Resume to recover."*
  — matches G3's "never a false 'Paused'" bar, and here it's a TRUE paused for every single one.
- Clicked the same button again (now labeled "Frozen — resume"): `docker ps` afterward shows
  **zero** jails still paused — all 13 correctly un-paused.

This is the fullest possible confirmation of G3 short of a genuinely adversarial mid-verification/
mid-merge engage (not attempted this pass) — engage and resume both driven by a real sidebar click,
both checked against Docker directly, not just the app's own say-so, matching the Mac run's
`ee9be50` fix (ISSUES-LOG #17) and now also its H1 resource-honesty and F4 cascade-visibility
claims, all on this substrate.

## 8. Closing tally

**Runbook §4 cycle (spawn → verify → gate → cascade → pause/kill-switch → merge): every
numbered step exercised, all PASS** — steps 1-2 via RPC, steps 3-4 via both RPC AND real UI clicks
(Review/Merge), step 5 (changed-test-command gate + reject) via RPC, step 6 (stale cascade) via
RPC, step 7 (pause/resume) via RPC, step 8 (kill switch) via BOTH RPC and real UI clicks.
Runbook §5 (real claude-code agent, needs a human login) and §6 (restart resilience, needs a
clean graceful-quit re-verify) were not reached this pass — flagged as open below, not silently
skipped.

**Matrix rows touched this pass:** A1 (repo provision, RPC) partial; C1 (PTY output, real UI)
confirmed; D1/D3/D7 (verify pass path + changed-test-command gate) confirmed via RPC; E2/E3 (Review
reachable, verified-@ stamp) confirmed via real UI; F1/F2/F3/F4 (merge three-step, non-ff refusal,
mirror refresh, stale cascade) all confirmed, F1/F2 via real UI; G1 (pause/resume) confirmed via
RPC with real docker cross-check; G3 (kill switch) confirmed via **real UI** with real docker
cross-check, both directions; H1 (resource honesty) confirmed via real UI. Untouched this pass:
A2/A3, B1-B5 (BYOK/OAuth — needs a human login), D2/D4/D5/D6, E1/E4-E7, F5/F6, G2/G4, H2/H4-H7,
I1-I5 — a large remaining surface, honestly not covered, not silently assumed green.

**Bugs found this pass**, severity and status:
- **W1 — sandbox image runtime build fails (`TARGETARCH` unset) — HIGH — FIXED** (`d8b371e`,
  50/50 tests green).
- **W2 — repository-list rows have no UI-Automation activation path — MEDIUM — OPEN** (logged,
  worked around via RPC + a config-file edit, not source-fixed).
- **W3 — auto-detected repo mislabeled `.git` instead of its folder name — LOW/cosmetic — OPEN.**
- **W4 — git "dubious ownership" blocks every fetch/merge against the WSL2 UNC mirror remote —
  HIGH — OPEN** (this session's `safe.directory=*` is a local unblock only, not a fix; real fix
  needs someone to nail the exact path form git will match).
- **W5 — rejected/discarded-by actor renders as a bare `uid:1000`, not a human identity — LOW —
  OPEN.**
- **Matrix-doc correction (not a bug):** `full-test-matrix.md`'s G3 wording ("BeginMerge/
  DiscardEntry refused while frozen") is stale against deliberate, documented, correct code —
  Discard/Reject are intentionally NOT kill-switch-gated. Flag for whoever owns the matrix doc.

**Positive re-confirmations** (Mac-run fixes holding on this substrate): `ee9be50` (kill-switch
resume un-pausing a real jail) — RPC AND real UI, both directions; `c1e4c3e` (queue count-header,
"N in play · N in history") — visually confirmed with an 11-entry queue; the general merge-queue
lifecycle (`7497202` display ordering, `978db19`/`a972b02` provision-binding fixes) — implicitly
exercised throughout without any of the symptoms those fixes addressed recurring.

**Git status:** clean except two untouched, unrelated pre-existing untracked files
(`mainguard-findings.md`, `mainguard-security-audit-phase2.md`, dated 2026-08-06, out of scope for
this pass). All work committed to `port/macos` and pushed after every meaningful step — no
uncommitted work at risk.

**What's still open / next leg's starting point:**
1. W4 (git dubious-ownership/UNC) — the highest-value follow-up; blocks every real merge for every
   Windows+WSL2 user until solved.
2. W2 (repo-list row activation) — a real Avalonia automation-peer gap worth its own investigation.
3. The real-agent leg (§5, needs a human to complete an OAuth login) and restart-resilience (§6,
   needs a clean graceful Exit → relaunch, not the process-kill used to pick up the config edit)
   were not reached — next session should start there.
4. The full A-J fresh pass (priority 3) was not attempted — this session prioritized the
   already-fixed-bug re-verification and the cycle spine per the brief's own stated order.

## 9. Restart resilience (runbook §6) — a genuine substrate difference from the macOS assumption

Clicked the real **Exit** menu item (found via the hamburger "Toggle menu bar" — a distinct button
from the sidebar's own collapse/expand toggle, which are two different controls at similar
positions and easy to confuse; cost real time this leg). A confirmation dialog appeared —
**"Exit Mainguard? 13 agents are still running. Exiting stops the Mainguard environment and
terminates them mid-task. Their work stays on their branches, but sessions cannot be resumed."**
— with Cancel / "Exit and stop agents".

**This is the finding, not a side note:** the runbook's restart-resilience assumption ("Quit the
app (NOT 'Stop all') — a plain quit; the daemon and jails outlive the UI") does **not** hold here.
Confirmed directly: after clicking "Exit and stop agents", `wsl -l -v` showed `MainguardEnv`
**Stopped**, and all 13 jails showed `Exited (255)` — the entire VM was torn down, not just the UI
process. Traced to `"StopVmOnExit": true` in `config.json` — a real, named setting, not a bug, but
one that makes a plain "Exit" on Windows behave like the Mac run's "Stop all" in consequence (every
live agent genuinely dies), because the Windows/WSL2 substrate's daemon lives inside a VM with a
real resource cost, unlike the macOS run's natively-hosted daemon. **The runbook was written from
the macOS run's perspective and its §6 assumption is substrate-specific, not universal** — worth a
correction for whoever maintains it, and worth deciding deliberately (not by accident) whether
`StopVmOnExit`'s default should differ from what shipped here.

**What DID work well, and re-confirms real fixes:**
- Relaunching correctly detected the stopped VM and showed an honest, step-by-step **"Getting
  Mainguard ready"** boot sequence (Start the environment → Connect to the daemon → Apply updates →
  Check sandbox images), each step checked off as it completed — good OOBE-honesty-principle UX,
  matching H5's spirit even though this isn't literally first-run.
- The **persisted merge queue survived completely intact** — all 11 entries (7 in play, 4 in
  history), full state, full history — confirmed the daemon's SQLite-backed queue is genuinely
  durable across a full VM teardown, not just an in-memory convenience.
- **Coordinator correctly showed "No coordinator running"** rather than something misleadingly
  claiming to still be attached — honest given the real state (no jail survived to attach to).
- **The stranded-entry reconciliation re-confirms the Mac run's fix directly**: `Working` entries
  (whose jails were destroyed) now show the exact honest message *"the agent's sandbox is gone —
  resume the entry to give it one, or discard it"* — matching ISSUES-LOG #23/#24's fix, holding
  after a substantially more destructive event (full VM teardown, not just a daemon restart) than
  the Mac run tested.
- New, worth a closer look next session (not chased further this pass): previously-clean `Verified`
  entries now additionally show *"flagged-change review has not run for this branch (no
  acknowledgment record)"* post-restart — unclear yet whether this is an honest new gap the
  reconciliation correctly surfaced (the flagged-change check itself needs a live sandbox to run,
  and can't have after a teardown) or a reconciliation regression that over-flags. Logged as an
  open question, not a confirmed bug either way.

**Side notes from this leg:**
- The sidebar's collapse/expand state is persisted to `config.json`'s `SectionRailExpanded` — an
  earlier accidental click (chasing the wrong "hamburger") left it collapsed, and it stayed
  collapsed across two further app kills/relaunches until edited back in the config directly. Not
  a bug — a persisted preference behaving exactly as designed — but worth knowing for whoever
  automates this UI next: the hamburger icon (top-left, toggles the **top menu bar** —
  Select Repo/Settings/Exit) and the sidebar's own collapse toggle (further down, toggles icon-only
  mode) are two separate controls that look similar and are easy to hit by mistake.
- Collapsed sidebar icons expose **no accessible name at all** to UI Automation (not even a
  fallback) — worth folding into W2's accessibility finding if someone picks that up: the gap isn't
  only list rows, collapsed nav icons lose their name too.

## 10. The real-agent leg (runbook §5) — claude-code, end to end, with a correction to a standing assumption

Selected `claude-code 2.1.223` (already installed) and clicked **Start coordinator** for real. It
booted straight to **"Welcome back Daniel!"** — Opus 5 (1M context) · Claude Max ·
`daniel.sazykin@gmail.com`'s Organization — **no login prompt at all**, confirming B2's login
persistence: the OAuth session from a prior install/session was harvested and reused, exactly as
`<data-root>/Keyring/cli_login_claude-code.*` is supposed to provide. The user's own request to
"send a notification when you need my login" never triggered, because it wasn't needed.

### Correction: real keyboard input DOES reach a live jailed PTY on this substrate

The runbook states as fact: *"synthetic keystrokes do not reach a jailed PTY — a human at the
keyboard, or `SendPromptAsync` [RPC], drives it."* First tried `SendPromptAsync` (RPC) with a real,
verifiable task — it landed the text into the composer's buffer but **never submitted it** (no
`\r`/Enter equivalent actually reached the CLI; the text just sat there unsent, including a second,
literal, real-keyboard-typed line appended after it). Prompted directly by the user to actually test
real UI input rather than accept the RPC shortcut: clicked into the composer (`SendInput` absolute
click — coordinates verified against the visible prompt), typed the task via `SendUnicodeText`
(`Win32.SendInput` with `KEYEVENTF_UNICODE`), then sent a real `VK_RETURN` via `SendVk`. **This
worked** — the accumulated composer text (the RPC-sent task text plus the real-typed line) was
submitted together, and Claude Code began working immediately: "I'll start by looking at the
existing files" → read/edited `src/calc.js` and `test.js` → ran `node test.js` → hit two real
**permission prompts** (shell-command approval for `git commit`, retried once after it
self-diagnosed and fixed a missing git identity in the jail) — both answered via real keyboard
(`1` + Enter) exactly as a human would. **The task completed for real**: `git log` on the agent's
own branch (`agent/8697bec3d2a44dc8a9069c3302a5ae77`, mirror-side, confirmed via `wsl -d
MainguardEnv`) shows commit `2cc06d9 feat: add subtract function to calc module`.

**This is a genuine, substrate-relevant correction, not just a "yes it works" note**: whatever
limitation the runbook's warning was written against (most plausibly the Mac run's own
`osascript`/`System Events keystroke` mechanism — ISSUES-LOG #6 from that run documents exactly
that path dropping characters, a different failure mode from "doesn't reach the PTY at all") does
not apply to Windows `SendInput`-based keyboard delivery. **Mouse input is the one that never
reaches this app** (confirmed repeatedly this session — clicks, double-clicks, and now scroll-wheel
events all silently no-op), **keyboard input reaches it fine**, including into a live jailed PTY.
Worth rewriting the runbook's C1 note to be substrate-specific rather than a blanket claim.

### Verification — PASS

`RunVerificationAsync` on the real commit: `Ran=true Passed=true Reason="verified against
main@7400e105"`, queue state `Verified`, `CanMerge=true`. Matches runbook §5 step 5's assertion
exactly (the only remaining unexercised half of that step is clicking Merge specifically on THIS
entry through the UI rather than RPC — already proven mechanically identical to the F1 merge done
earlier in this pass on a `scripted` entry; not repeated here to avoid re-litigating an already-
confirmed mechanism, especially since the live queue rail had 8 entries and this one sat below the
fold with no way to scroll to it via synthetic mouse wheel — see below).

### Side finding: merge-queue rail scrolling is also blocked by the mouse-input limitation

Wanted to scroll the 8-entry queue rail to reach the claude-code entry's Review button directly (it
was correctly present — the header's count, "8 in play," matched exactly: the 6 visible rows + the
current coordinator + the claude-code entry). `mouse_event(MOUSEEVENTF_WHEEL, ...)` produced no
visible scroll — consistent with, not a new instance of, this session's standing finding that no
synthetic mouse input of any kind reaches this app. Not logged as a new bug — filed under the same
root cause as the rest of the mouse-input gap.
