# Issues log — live UI walkthrough, Windows/WSL2, 2026-08-24

Mirrors the Mac run's log format. Cross-referenced to `WALKTHROUGH.md`. Status: **OPEN** (logged,
not fixed — non-blocking), **FIXED** (blocking, fixed inline this pass, commit noted), or
**CONFIRMED** (re-verified from the Mac run's ISSUES-LOG).

---

### W1. [FIXED — commit `d8b371e`] Sandbox image runtime provisioning build fails on this Docker engine — `TARGETARCH` never populated

- **Where:** substrate prerequisites (runbook §1) / first `SpawnAgentAsync` call.
- The daemon refused every spawn with `FailedPrecondition`: "Mainguard OS sandbox image(s) need
  provisioning (outdated: mainguard-agent-base:latest)". Its own build attempt
  (`SandboxImageProvisioner`) failed: `images/mainguard-agent-base/Dockerfile`'s per-arch
  nix-installer step reads `TARGETARCH`, got an **empty string**, and hit the Dockerfile's `*)`
  fallback (`exit 1`).
- **Root-caused, not guessed:** `SandboxImageCommands.BuildImage` (`Mainguard.Agents/Agents/
  Bootstrap/SandboxImageProvisioner.cs:64`) shells out to `docker build` with no `--build-arg`,
  no `DOCKER_BUILDKIT`, no platform flag at all — confirmed by reading the exact argv the existing
  unit test hard-asserts. `TARGETARCH` is a BuildKit-automatic build-arg; this engine
  (`20.10.24+dfsg1` inside `MainguardEnv`) doesn't populate it for a plain `docker build` the way
  the Mac run's OrbStack/Docker Desktop apparently does. Verified the network/pin itself is fine —
  manually curling the pinned Nix installer URL inside an identical `debian:bookworm-slim`
  container returned HTTP 200 with a SHA-256 matching the Dockerfile's pin exactly, and a manual
  `docker build --build-arg TARGETARCH=amd64 ...` of the unmodified Dockerfile succeeded clean.
- **Not a G-16 violation** (initially suspected one, was wrong): the file's own doc comment
  clarifies this is a *provisioning-time* build, and G-16 forbids only *agent-runtime* builds — a
  false alarm corrected during the same investigation, logged here so it isn't relitigated.
- **Fixed** in `d8b371e`: `SandboxImageCommands.BuildImage` now passes `--build-arg
  TARGETARCH=amd64` explicitly — safe because every build through this code path runs inside
  `MainguardEnv`, which is always x86_64 (arm64 is the separate macOS-host substrate, a different
  code path per the existing doc comments). Updated the two hard-coded-argv unit tests
  (`SandboxImageProvisionerTests.cs`) accordingly. `dotnet test Mainguard.Tests --filter
  FullyQualifiedName~SandboxImage`: 50/50 green.
- Severity: **HIGH** — this silently blocks EVERY agent spawn on a fresh/updated install on this
  class of Docker engine, with a misleading-sounding remedy in the daemon's own error text ("It
  starts after launch and takes several minutes" — implies patience will fix it; it never would
  have, since the same build fails identically every retry).
- **Live-reverified**, not just unit-tested: after the fix, `SpawnAgentAsync` and the whole
  spawn→verify cycle ran clean through the RPC harness (see WALKTHROUGH.md §3).

### W2. [FIXED — commit `5204e97`] Repository-list rows are not activatable via UI Automation or synthetic mouse input

- **Where:** the "Select Repo" picker (`RepoPickerWindow`), while trying to open the fixture repo
  through the real UI (not RPC) for the first time.
- Every named repo entry (`AGY Cortex`, `demo`, ..., the newly-added `e2e-fixture`) exposes to UI
  Automation only a bare `TextBlock` — `ControlType.Text`, `ClassName=TextBlock`,
  `IsKeyboardFocusable=False`, supporting only `ScrollItemPattern` — with **no**
  `InvokePattern`/`SelectionItemPattern` anywhere between it and the containing `ListBox` /
  `ScrollViewer` / `Window` (walked the full ancestor chain via `TreeWalker`; confirmed each level's
  supported-patterns list directly). Neither a genuine `SendInput`-based double-click (cursor
  position cross-checked correct via `Cursor.Position` before clicking) nor UI-Automation `Invoke`
  on the text element opens the repo — the main window stays on "Select a repository to begin"
  through every attempt.
- This is a real accessibility gap, not just an automation inconvenience: the same UIA tree a
  screen reader would see offers no way to activate a list row either.
- Cross-checked that synthetic mouse input isn't universally broken in this session: UI Automation
  `InvokePattern` DID work for the picker's own toolbar icons (the folder-browse "auto-detect"
  button) and for the main window's "Dismiss" button on the stale reopen-last-repo prompt;
  `SendMessage(hwnd, BM_CLICK, ...)` worked for the native Win32 "Select folder" common dialog's
  own buttons. So the gap is specific to this list's row-level automation peers, not a broad
  environment problem.
- **Originally deferred** — pivoted to the RPC harness (`ProvisionRepoAsync`/`SetActiveRepo`) for
  repo binding, exactly as the runbook recommends for setup steps ("prefer RPC... over GUI
  automation, which is OS-specific and brittle").
- **Fixed (commit `5204e97`).** The rows were never `ListBox` items — the template renders a plain
  `Border`/`Grid` with raw pointer handlers, which is why nothing on the chain had a peer. The row
  surface is now `Mainguard.App.Shell/Controls/RepoRow`, a `Grid` subclass carrying a
  `ControlAutomationPeer` that reports `AutomationControlType.ListItem` and implements
  `IInvokeProvider`, plus Enter/Space activation and `Focusable=true`. Not wrapped in a `Button`
  on purpose: a Button captures the pointer press that the select-then-drag gesture depends on.
  `Headless/RepoPickerAccessibilityTests` pins it, finding the row by walking up for an
  `IInvokeProvider` exactly as this investigation's `TreeWalker` did.
- Severity: medium — doesn't block the app (the RPC-equivalent action, opening the LAST repo via
  the reopen-prompt, does work — see W2 note below), but anyone driving this list by keyboard or
  assistive tech has no way to open a NEW entry after adding it, only ever the single
  most-recently-opened one via the reopen prompt.
- **Note:** the "Reopen Last Repository?" prompt's own Dismiss/Reopen buttons DO work (they're real
  named buttons with `InvokePattern`) — only the scrolling list of *all* repos lacked any activation
  path.
- **Related, found while fixing W2 and fixed with it:** every `Button.railItem` in the main window's
  section rail (and the Pro agent rail) had no usable accessible name. Avalonia does not fall back to
  `ToolTip.Tip`, and `ButtonAutomationPeer`'s `Content?.ToString()` fallback reported the literal
  string `"Avalonia.Controls.Grid"` — which is why "no name at all" is *almost* right: there is a
  name, it is junk, and a non-emptiness check would have called it healthy. Four
  `AutomationProperties.Name` bindings fix it; the guard in `ActivityBarRenderHarness` rejects
  `Avalonia.*` names rather than merely empty ones.

### W3. [FIXED, cosmetic] Auto-detected repository entry mislabeled as `.git` instead of its folder name

- **Where:** the picker's "auto-detect repositories" folder-browse flow (its own toolbar icon,
  tooltip "Select folder for auto-detecting repositories").
- Pointed the auto-detect browse dialog directly at a folder that IS itself a git repo
  (`e2e-fixture`, not a parent folder containing several repos). The newly-added list entry is
  labeled **`.git`** — the name of the repo's own `.git` subdirectory — instead of `e2e-fixture`,
  the repo's actual folder name.
- Contrast: every other entry in the list (`AGY Cortex`, `demo`, `GitLoom`, ...) is correctly
  labeled by its containing folder's name — those were all found via scanning a *parent* folder
  (`Code/`) that contains multiple repos, the more common case. The bug specifically triggers when
  the scan ROOT passed to auto-detect is itself a repo.
- Data is otherwise fine — the entry is real, selectable, and (per the RPC harness) points at a
  correctly-provisioned handle; this is purely a display-label bug, most plausibly the auto-detect
  scanner using the found `.git` directory's own name as the display name rather than its parent
  when the scan root and the repo root coincide.
- Severity: low — purely cosmetic; does not affect provisioning, verification, or merge (confirmed
  via the RPC harness, which addresses repos by handle/path, never by this display label).
- **Root cause + fix (follow-up pass).** `MainWindowViewModel.ScanAutoDetectFolderAsync` walked only
  the chosen root's children and grandchildren, never testing the root itself; `GitService
  .IsGitRepository` (`LibGit2Sharp.Repository.IsValid`) also returns true for a bare `<repo>/.git`
  path, so a root that was itself a repo produced exactly one "found repo": its own `.git` dir. Fix
  lives in a new, independently testable `AutoDetectScan.cs`: the root is now returned under its own
  folder name (no descent) when it is itself a repo; `GitService.IsGitRepository` also gained a
  defensive guard refusing any path whose leaf is exactly `.git`, name-exact so the agent platform's
  bare mirrors (`<hash>.git`) are unaffected.
- Status: **FIXED** (`Mainguard.App.Shell/Services/AutoDetectScan.cs`,
  `Mainguard.Git/Services/GitServices.cs`; regression pinned by
  `Mainguard.Tests/AutoDetectScanTests.cs`, including
  `ADotGitDirectory_IsNeverItselfARepository`).

---

## Priority-1 re-verification (Mac-run FIXED/CLOSED bugs) — status

Not yet started at the time of this log snapshot; the substrate-readiness blocker (W1) and the
GUI-automation calibration (W2/W3 discovered along the way) consumed the setup phase. Tracked
separately in WALKTHROUGH.md's running log as each one is actually re-driven live.

### W4. [FIXED, was HIGH — real, blocking, confirmed] Git "dubious ownership" blocks every fetch/merge against the WSL2 UNC mirror remote

- **Where:** runbook §4 steps 3-4 (`BringBranchLocalAsync`/`ConfirmMergeAsync`), first attempt.
- Every fetch against the daemon-provisioned sync remote (`mainguard-vm`, a
  `\\wsl.localhost\MainguardEnv\...` UNC path) fails with git's own
  `fatal: detected dubious ownership in repository at '...'` until `safe.directory` is configured
  for that path.
- **Root-caused, not a harness artifact:** grepped `Mainguard.Agents`/`Mainguard.Agents.UI`/
  `Mainguard.Git` end to end for `safe.directory`/`safe\.directory`/`dubious` — zero hits anywhere.
  `SyncRemoteRegistrar.cs` (the only code that registers this remote) does a plain
  `IGitService.AddRemote`/`SetRemoteUrl`, nothing else. This will reproduce identically for every
  real user's first merge on a fresh Windows+WSL2 install, not just this harness.
- **Attempted the narrow fix, it silently failed:** `git config --global --add safe.directory
  '<exact UNC path>'` did not unblock the fetch even though the value stored in `.gitconfig` and
  the path in git's own error text were character-identical side by side. `safe.directory=*` DID
  unblock it immediately for the same exact path. This points at a git-for-Windows-specific
  path-normalization quirk with `\\wsl.localhost\...`-style UNC paths that defeats
  `safe.directory`'s exact-string match — a real, reproducible git behavior, not a fluke (retested
  across a fresh repo handle/mirror path with the same result).
- Severity: **HIGH** — this blocks the single most important guarantee of the whole product (a
  verified agent branch actually reaching `main`) for every Windows+WSL2 user, on the very first
  merge, with an unhelpful raw git error and no Mainguard-authored guidance pointing at the fix.

#### RESOLVED — root-caused and fixed (`Mainguard.Git/Services/UncRemoteTrust.cs`)

Measured live against git 2.45.1.windows.1 and the real MainguardEnv mirror. **Two independent
findings**, the first of which invalidates the obvious fix:

1. **`safe.directory` is ignored in command scope — including `*`.** Git reads it through
   `read_very_early_config()`, which sets `ignore_cmdline = 1`. Measured, all against the same live
   mirror with no config present: `git -c safe.directory=<exact path>` → **fails**;
   `git -c safe.directory=*` → **fails**; `GIT_CONFIG_COUNT=1 KEY_0=safe.directory VALUE_0=<path>` →
   **fails**; same with `*` → **fails**. Only file-based *system*/*global* scopes are honoured
   (`GIT_CONFIG_SYSTEM` → **works**, `GIT_CONFIG_GLOBAL` → **works**, real `.gitconfig` → **works**).
   So the tempting one-line `-c safe.directory=…` patch would have compiled, shipped, and changed
   nothing.
2. **Why the "character-identical" exact path didn't match.** It wasn't a UNC normalization quirk at
   all. The value had reached `.gitconfig` under-escaped: the file held
   `directory = \\wsl.localhost\\MainguardEnv\\…` (two leading backslashes), and since `\` is a
   config-file **escape character**, git parsed that as `\wsl.localhost\MainguardEnv\…` — **one**
   leading backslash, which is not a UNC path and can never match. `git config --get-all
   safe.directory | cat -A` shows the single backslash; the side-by-side comparison that "looked
   identical" was comparing the *escaped file text* against the *unescaped error text*. Re-adding the
   same path so the file holds four leading backslashes makes it read back as `\\wsl.localhost\…` and
   the fetch **succeeds**. The forward-slash spelling `//wsl.localhost/…` does **not** match — the
   literal Windows spelling is required.

**The fix**: `UncRemoteTrust.RunGitTrustingRemote(repoPath, remoteName, args…)` reads
`remote.<name>.url` and, only when it is a UNC repository path, writes a throwaway config file naming
that one exact directory in `safe.directory` and passes it as `GIT_CONFIG_SYSTEM` for that single
child process. The shim `include.path`s the real system config so the user loses no setting, and
**nothing is written to the user's own config at any scope** — no `*`, no global entry. Wired into
the three client-side sync-remote fetches: `BringLocalService`, `ForegroundMergeService` (the merge
path), and `ExternalPrMergeService`. Non-UNC remotes and non-Windows hosts take an early return to
the unmodified `GitService.RunGit`.

**Live verification** (`BringLocalService.BringLocal` driven as a real Windows process against the
real mirror, with **zero** `safe.directory` entries in any scope — the `*` workaround removed):
plain `git fetch` in the same fixture, same UNC path → `exit=128 fatal: detected dubious ownership`;
`BringLocal(...)` immediately after → `Done=True`, `refs/heads/agent/025f8540…` created at the
mirror's real tip `9cc1365 feat: add note module`. The global `*` relaxation this walkthrough relied
on has been **removed from this machine** and is no longer needed.

### W5. [FIXED, low, cosmetic] Rejected/discarded-by actor renders as a bare `uid:1000`, not a human-readable identity

- **Where:** `RejectEntryResponse.rejected_by` / `DiscardEntryResponse.discarded_by`.
- On the Mac run this field read `os:danielsazykin` (ISSUES-LOG #13, 2026-08-20). On this machine
  the identical field/flow returns **`uid:1000`** — the WSL2 VM's internal Linux user id, which
  means nothing to a Windows user looking at their own audit trail.
- Not chased to a root cause this pass (would need reading `_identity.Resolve(context)`'s
  Windows-side implementation) — flagged for follow-up. Correctness is unaffected (the value is
  still a real, stable actor identifier), only its human-readability regresses on this substrate.
- Severity: low.
- **Root cause + fix (follow-up pass).** `PeerCredentialIdentityResolver` branched on
  `IsOSPlatform(Linux)` and returned `uid:{geteuid()}` there, `os:{Environment.UserName}`
  everywhere else. On Windows/WSL2 mainguardd runs *inside* the VM, so it took the Linux branch;
  the Mac daemon runs natively on a non-Linux host, so it took the other. **The framing above is
  half wrong and worth correcting:** this is not an "identity regression", because the resolved
  value is not the caller's identity on *either* platform — loopback TCP carries no peer
  credential, so it is the daemon's own account, a constant. `os:danielsazykin` only looked like a
  human because on macOS the daemon happens to run as one. The real defect was narrower and real:
  the `uid:` shape was a leftover of the original (retracted in MG-16) `SO_PEERCRED` framing — a
  peer credential is a *number* — and MG-16 fixed only the documentation, leaving the format. No
  technical reason survived it: `Environment.UserName` resolves through `getpwuid`, not
  `$USER`/`$LOGNAME` (verified — it ignores those even when set to another value, so it is not
  env-spoofable), and the VM image gives the service account a real passwd entry
  (`useradd … mainguard`) that the unit runs as (`User=mainguard`). Unified on `os:<name>` for all
  platforms; on Windows/WSL2 this now reads `os:mainguard` — **the service account, not the
  Windows user**, which is the honest answer and all this field ever meant. `uid:<euid>` survives
  only as a last resort for a euid with no passwd entry, where `Environment.UserName` returns `""`
  (verified) and a bare `os:` would be a blank actor.
- Status: **FIXED** (`Mainguard.Server/Auth/ApproverIdentityResolver.cs`; regression pinned by
  `ApproverIdentityDaemonDerivedTests.PeerCredentialResolver_ReportsTheDaemonsOwnIdentity_ConstantForEveryCaller`,
  which previously asserted the `uid:` literal on Linux).

### Note — `full-test-matrix.md`'s G3 wording is stale, not the kill-switch code

While driving G3 (kill switch) via RPC, `DiscardEntryAsync` succeeded on a live, frozen queue —
initially read as a regression against the matrix's own line: **"freezes the queue (BeginMerge/
DiscardEntry refused while frozen)."** Reading `MergeQueueGrpcService.cs` before touching anything
shows this is deliberate: `DiscardEntry`'s own doc comment states "The kill switch does NOT gate
it. Freezing the queue stops merges; it is not a reason to forbid the human from tidying an entry
that can no longer merge either way," and `RejectEntry` carries the identical rationale for review
verdicts. Only `BeginMerge` and `ConfirmMerge` — the two operations that actually touch git — call
`ThrowIfFrozen`. **The matrix text needs updating, not the code** — flagging here rather than
"fixing" correct, intentional behavior. Kill-switch engage/resume itself (both jails pausing,
both correctly un-pausing) re-confirms the Mac run's `ee9be50` fix (ISSUES-LOG #17) holds under
this machine's Docker Desktop/WSL2 backend.

### Confirmed via real UI clicks: kill switch (G3) full cycle, resource-honesty (H1), Review/Merge (E2/H3/F1/F2)

- **G3 kill switch**, sidebar "Stop all" → real click (its label text has no `InvokePattern` but
  its parent `Button` does): engaged, button flipped to "Frozen — resume", **all 13** live jails
  showed `docker ps` `(Paused)` (cross-checked directly, not just trusting the UI), Resources panel
  updated every row to `Paused` with the honest message "Kill switch engaged — jail paused,
  terminal input severed. Resume to recover." Clicked again: all 13 un-paused, confirmed via
  `docker ps` again. Re-confirms `ee9be50` (ISSUES-LOG #17) holds under Docker Desktop's WSL2
  backend via the real button this time, not just RPC.
- **H1 resource honesty**: real Resources panel showed the exact unmetered-spend explainer text,
  genuinely-measured `0%`/`0.0 GB` (not the unmeasured-`—` case) for idle scripted jails, and — a
  nice bonus — live "Rebasing onto the new main after a merge" task text on the stale-cascade
  entries from earlier RPC testing, proving the cascade is user-visible, not just internal.
- **E2/H3/F1/F2**: Review cockpit opened with the correct `Coordinator (scripted)` title (not a
  raw GUID) via a real click; Merge correctly refused a genuinely non-fast-forwardable entry
  (an artifact of this session's own heavy parallel RPC testing creating divergent sibling
  branches — confirmed via `git log --graph`, not a product bug) without moving `main`; a second,
  genuinely fast-forwardable entry merged successfully via real clicks, `main` advanced to the
  right sha, and `node test.js` still passed afterward.

All of the above via `InvokePattern`/`BM_CLICK` — this session never got a raw `SendInput` mouse
click to register with this Avalonia app, on any element, including a plain, simple button (the
hamburger menu) tested specifically to rule out "maybe it's just list rows." Worth flagging for
whoever picks up W2: if a future pass wants real mouse-driven testing (not UI-Automation-driven),
this environment's `SendInput` path needs its own investigation first.

### W6. [FIXED, MEDIUM — real, confirmed] Settings → Agent CLIs reports an installed CLI as "Not installed" when its installed version differs from the currently-pinned one

- **Where:** Settings → Agent CLIs (P2-22 adapter-channel UI), real click.
- Claude Code had been in active use all session (spawned, driven through a real task, verified,
  merged — see §10 of WALKTHROUGH.md) at version **2.1.223** (confirmed via the daemon's own
  `ListInstalledAdapters` RPC and the VM's `adapters/registry/claude-code.json`). The Settings
  panel nonetheless showed **"Claude Code v2.1.218 — Not installed"** with an `Install` button.
- **Root-caused:** `AgentCliInstaller.IsInstalledAsync`
  (`Mainguard.Agents/Agents/Adapters/AgentCliInstaller.cs`) runs a health-probe command in the VM
  and checks `probe.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, Ordinal)` — an
  **exact substring match against the currently-offered manifest version** (2.1.218), not "is some
  working version installed." Since the actually-installed 2.1.223 doesn't contain "2.1.218", the
  probe fails and the CLI reads as not-installed, even though it demonstrably works (this same
  install drove a real end-to-end agent cycle minutes earlier in this same session).
- Severity: medium — cosmetic/confusing (a user could be told to "Install" something already
  working, or click Install and have it no-op/reinstall unnecessarily), not a correctness or
  security issue — the actually-installed adapter is unaffected and still fully functional.
  - **Root cause + fix (follow-up pass).** Resolved the design question in favor of "installed" =
  "a healthy version is present," with drift surfaced honestly rather than hidden. Exit status
  (from the manifest's own verification notes) already distinguishes real absence and the known
  `claude-code`/`opencode` npm-launcher-without-native-binary failure mode from a working CLI, so
  the version-substring match wasn't load-bearing for that; it just also had to parse whichever of
  the two real stdout shapes (`2.1.223 (Claude Code)` vs `codex-cli 0.145.0`) a given adapter uses.
  `AgentCliInstaller.IsInstalledAsync` → `ProbeInstalledVersionAsync`, returning the parsed version
  (or null) instead of a boolean exact-match. The Settings row now reads the truthful
  `InstalledLabel` (e.g. "Installed — v2.1.223 is what runs here; this Mainguard build pins
  v2.1.218") instead of a hardcoded "verified at the pinned version" string. `AnnotateUpdatesAsync`
  (npm-registry-vs-pin) was deliberately left untouched — it isn't the drift mechanism and routing
  drift through it would have offered an `Update` that `ApplyUpdateAsync` guarantees to refuse
  (MG-14 blocks downgrades). `AdapterChannel.cs`'s install-idempotence/verification checks, which
  correctly need an exact-pin match, are unaffected.
- Status: **FIXED** (`Mainguard.Agents/Agents/Adapters/AgentCliInstaller.cs`,
  `Mainguard.Agents.UI/ViewModels/AgentCliRowViewModel.cs`,
  `Mainguard.Agents.UI/Views/AgentCliSettingsView.axaml`; regression pinned by
  `Mainguard.Tests/AgentCliUiTests.cs`'s
  `Settings_InstalledCliAheadOfThePin_ShouldReadAsInstalled_NotAsMissing`, plus three guard cases
  proving the loosened predicate didn't take genuine-absence detection with it).

### Live re-verification of W2-W6 (2026-08-25) — see WALKTHROUGH.md §13

All five fixes above were re-driven through real UI (not RPC) on this same substrate after
merging: W2 via the same UIA `TreeWalker` methodology that found the original bug (now finds
`InvokePattern` at depth 0, plus a genuine keyboard-Enter activation), W3 via a fresh throwaway
repo (not the already-added, dedup-prone `e2e-fixture`), W4 via a real **"Bring local"** click on a
live `Verified` queue entry with ground-truth confirmation (`git branch -a` shows the real fetched
branch; `.gitconfig` gained no new `safe.directory` entry), W5 via a real **Reject** click with the
daemon's own log line as ground truth (`by=os:mainguard`, `rejected_by=os:mainguard`), and W6 via
Settings → Agent CLIs now showing the correct drift-aware label. All five hold. Full detail in
WALKTHROUGH.md §13.

### Note — repo-picker toolbar icons and category headers share W2's original bug, unfixed

Found while re-verifying W2: the repo picker's own toolbar buttons (add/clone/auto-detect/refresh)
and its category-header rows (`vs_code`, `Personal`, `Work`) still report the fallback
`"Avalonia.Controls.PathIcon"` / `"Avalonia.Controls.Grid"` accessible name — the same defect class
W2 fixed for the section rail and the repo rows themselves, just never extended to these. Not
fixed this pass (outside the scope of the requested fix-verification); flagged for a follow-up
pass through `RepoPickerWindow.axaml`'s toolbar and category-header templates.
