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

### W2. [OPEN, real & reproducible] Repository-list rows are not activatable via UI Automation or synthetic mouse input

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
- **Not fixed this pass** — pivoted to the RPC harness (`ProvisionRepoAsync`/`SetActiveRepo`) for
  repo binding, exactly as the runbook recommends for setup steps ("prefer RPC... over GUI
  automation, which is OS-specific and brittle"). A real fix means giving Avalonia's `ListBox` item
  containers proper automation peers/keyboard activation, which is a UI-layer change, not a
  same-pass patch.
- Severity: medium — doesn't block the app (the RPC-equivalent action, opening the LAST repo via
  the reopen-prompt, does work — see W2 note below), but anyone driving this list by keyboard or
  assistive tech has no way to open a NEW entry after adding it, only ever the single
  most-recently-opened one via the reopen prompt.
- **Note:** the "Reopen Last Repository?" prompt's own Dismiss/Reopen buttons DO work (they're real
  named buttons with `InvokePattern`) — only the scrolling list of *all* repos lacks any activation
  path.

### W3. [OPEN, cosmetic] Auto-detected repository entry mislabeled as `.git` instead of its folder name

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
- **Not fixed this pass** — cosmetic, low severity, first reproduction (no prior art in the Mac
  run's log — genuinely new, not a re-verification).
- Severity: low — purely cosmetic; does not affect provisioning, verification, or merge (confirmed
  via the RPC harness, which addresses repos by handle/path, never by this display label).

---

## Priority-1 re-verification (Mac-run FIXED/CLOSED bugs) — status

Not yet started at the time of this log snapshot; the substrate-readiness blocker (W1) and the
GUI-automation calibration (W2/W3 discovered along the way) consumed the setup phase. Tracked
separately in WALKTHROUGH.md's running log as each one is actually re-driven live.

### W4. [OPEN, HIGH — real, blocking, confirmed] Git "dubious ownership" blocks every fetch/merge against the WSL2 UNC mirror remote

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
- **Not fixed this pass** — the real fix needs someone to either find the exact string
  representation git will actually match for a WSL UNC path (may require testing quoted forms,
  a trailing-slash variant, or the `\\wsl$\` legacy alias instead of `\\wsl.localhost\`), or avoid
  UNC entirely (e.g. route the mirror through a mapped drive letter). This is exactly the kind of
  fiddly, easy-to-get-subtly-wrong git/OS-path interaction the standing instructions say deserves a
  dedicated pass rather than a guessed one-line patch.
- **Workaround used for the rest of this walkthrough:** `git config --global --add safe.directory
  '*'` on this machine only — a real (if narrow) security relaxation, not applied as a source
  change, so every merge/fetch step in this log from here on ran with that override active.
- Severity: **HIGH** — this blocks the single most important guarantee of the whole product (a
  verified agent branch actually reaching `main`) for every Windows+WSL2 user, on the very first
  merge, with an unhelpful raw git error and no Mainguard-authored guidance pointing at the fix.

### W5. [OPEN, low, cosmetic] Rejected/discarded-by actor renders as a bare `uid:1000`, not a human-readable identity

- **Where:** `RejectEntryResponse.rejected_by` / `DiscardEntryResponse.discarded_by`.
- On the Mac run this field read `os:danielsazykin` (ISSUES-LOG #13, 2026-08-20). On this machine
  the identical field/flow returns **`uid:1000`** — the WSL2 VM's internal Linux user id, which
  means nothing to a Windows user looking at their own audit trail.
- Not chased to a root cause this pass (would need reading `_identity.Resolve(context)`'s
  Windows-side implementation) — flagged for follow-up. Correctness is unaffected (the value is
  still a real, stable actor identifier), only its human-readability regresses on this substrate.
- Severity: low.

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
