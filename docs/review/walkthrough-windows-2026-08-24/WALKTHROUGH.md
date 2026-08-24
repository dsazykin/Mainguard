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
