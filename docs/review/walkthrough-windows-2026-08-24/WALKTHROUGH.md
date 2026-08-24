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

