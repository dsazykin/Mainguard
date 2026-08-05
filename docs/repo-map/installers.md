<!-- Extracted verbatim from the AGENTS.md Repository Map. Keep current: when you add, move, or delete a file, update its entry here. -->
# `installer/` — OOBE, elevated helper, uninstaller

The three installer projects. `Mainguard.Installer.Elevated` is the ONLY elevated component.

- **`installer/Mainguard.Installer`** (P2-21) — the OOBE orchestration driver: runs
  `SystemDiagnostics` (hard-stop on any fail), drives the `OobeStateMachine`, relaunches the elevated
  helper ONCE at the "Construct Sandbox" step (`RunAsElevationLauncher` — the single UAC prompt), and
  delegates the VM import to the P2-05 `MainguardOsBootstrapper`. **P2-48:** the shipped OOBE is now
  the in-app Avalonia wizard (`Mainguard.Agents.UI` `OobeWizardViewModel`/`OobeWizardView`, which
  drives the SAME `OobeStateMachine`); this project is retained only as a headless/dev orchestration
  fallback and is `WinExe` (no console window — owner UX rule: zero terminal windows anywhere in the
  flow). The real `ISystemProbe`/`IWslStatusProbe`/`IElevationLauncher`/`IDaemonHealthProbe` impls
  (`WindowsSystemProbe`, `WslStatusProbe`, `RunAsElevationLauncher`, `WslDaemonHealthProbe`) **moved
  to `Mainguard.Agents/Agents/Bootstrap/`** in P2-48 so the wizard and this driver share ONE
  implementation.
- **`installer/Mainguard.Installer.Elevated`** (P2-21) — the tiny elevated helper (the only elevated
  component): does EXACTLY the two enumerated privileged actions (enable the two Windows features via
  the surfaced PowerShell; register the elevated resume Scheduled Task), then reports via exit code +
  `ElevatedHelperResult` JSON. No other privileged work ever moves here (plan §7). **P2-48:** `WinExe`
  + windowless child processes (no console flash); an `app.manifest` (`requireAdministrator`) +
  branded version resource (`AssemblyTitle`="Mainguard Setup") make the UAC consent dialog show a
  trustworthy product name, and the `SignMainguardExecutables`/pack signing hooks upgrade it to a
  verified publisher. Also: `installer/resume/register-resume-task.ps1` (the canonical ONLOGON/HIGHEST
  registration — never `RunOnce`; the resume task relaunches the GUI app back into the OOBE wizard).
  The reproducible payload pipeline lives in **`build/mainguardos/`** (Debian-bookworm `Dockerfile` —
  base pinned-by-digest + apt pinned at a frozen `snapshot.debian.org` timestamp `DEBIAN_SNAPSHOT` so
  a `packages.pinned.txt` package *name* resolves to one always-fetchable version — + `build.sh`
  deterministic repack → hash-stable `MainguardOS.tar.gz` with `/etc/mainguardos-release`; CI
  `payload-reproducible` double-builds), and the CVE cadence (bump `DEBIAN_SNAPSHOT`, keep base ≤
  snapshot) and the binding versioning discipline (every production daemon change bumps the App+Server
  versions in lockstep; every payload-input change cuts `build/mainguardos/VERSION` — both update
  tiers deploy on version comparison, never on content) in **`docs/mainguardos-updates.md`**. **The
  payload also carries the daemon so the imported VM boots orchestration, not just dockerd:**
  `build.sh` publishes `Mainguard.Server` (linux-x64, self-contained) **deterministically**
  (`Deterministic`+`ContinuousIntegrationBuild`, no ReadyToRun/single-file; deterministic portable
  PDBs DO ship so the daemon logs' `ex.StackTrace` carries `…() in <file>.cs:line N` — the PDB is
  deterministic too, so the daemon layer still keeps the whole tarball hash-stable) into
  `payload/daemon/` with the apphost renamed `Mainguard.Server`→**`mainguardd`** (so the process
  `comm` is exactly `mainguardd`, what P2-05 `pgrep -x mainguardd` matches); the `Dockerfile` `COPY`s
  it to `/opt/mainguard/` and ships **`mainguardd.service` (sets `Environment=HOME=/home/mainguard`
  explicitly as belt-and-braces; all per-user daemon state — token, SQLite, keyring, leader registry,
  plan store — lands under `$HOME/.mainguard` via `MainguardPaths`)** enabled; `/etc/wsl.conf` sets
  `[boot] systemd=true` (+ the dockerd boot command + `mainguard` default user) so systemd starts the
  loopback-`127.0.0.1:5250` daemon on first boot alongside dockerd (WSL2 `localhostForwarding` reaches
  it from the Windows app). `packages.pinned.txt` gained `systemd`/`systemd-sysv`, and (P2-48)
  `nodejs`/`npm` — the VM is the INSTALL HOST for the dynamic agent CLIs
  (`npm install -g --prefix /home/mainguard/mainguard/adapters <staged tarball>`); agents RUN those
  CLIs inside the jail against the agent image's own Nix-pinned node 22 (bumped from 20 to unblock
  gemini-cli/qwen-code and satisfy claude-code's engines), reached over the read-only
  `/opt/mainguard/adapters` mount, so bookworm's node 18 (EBADENGINE warnings only) is fine for
  installing; `.dockerignore` keeps built tarballs out of the context. **Packaging (P2-48 + Phase-3
  two channels)** lives in **`build/velopack/`** (`pack.ps1 -Channel client|pro` + `README.md`): from
  ONE commit, **client** = `dotnet publish Mainguard.Client.App` (no payload, agent-platform-free — a
  small/fast install) and **pro** = `dotnet publish Mainguard.Pro.App` (the Pro head's MSBuild targets
  co-locate the elevated helper + bundle `payload/MainguardOS.tar.gz` + daemon/images) → `vpk pack` →
  each channel emits its own self-updating Setup.exe + RELEASES feed under
  `artifacts/releases/<channel>`. `packId`/title/authors are **parameters** (defaults: pro
  `Mainguard`, client `MainguardClient`) — the persisted `Mainguard` packId value + install-lineage
  decision are LEFT for the Phase-4 owner call. The `.github/workflows/package-smoke.yml` Windows jobs
  assert the Pro co-location + branded UAC name AND the Client small/payload-free install.
- **`installer/Mainguard.Uninstall`** (P2-22 §J-6) — the thin `WinExe` clean-uninstall entry point.
  Parses the two user choices (`--keep-settings`, `--remove-sync-remote`) and drives the Core
  `Uninstaller` with the real Windows delegates (daemon stop via
  `wsl -d MainguardEnv -- pkill -f mainguardd`, Scheduled-Task removal via the `InstallerCommands`
  schtasks builder, appdata deletion, and — **P2-22 Q2** — the default-OFF `RemoveSyncRemoteAsync`
  delegate: reads the persisted repo list from `AppDbContext.Repositories.Path` UP FRONT (before the
  appdata step deletes the DB), resolves the sync-remote name via
  `new Wsl2AgentEnvironment().ResolveSyncRemote(...).Name` (SC-2 resolver — never a hardcoded name),
  and removes it through `GitService.RemoveRemote` via the Core `SyncRemotePurger`). The ordering,
  failure-tolerance, default-OFF gating and G-12 distro scoping all live in
  `Mainguard.Agents/Agents/Bootstrap/Uninstaller.cs` (unit-tested cross-platform); this exe only
  supplies the concrete side effects and prints the G-12 personal-distro diff. Real uninstall with a
  personal distro present is the manual matrix.

---

Back to [`docs/repo-map/README.md`](README.md) · [`AGENTS.md`](../../AGENTS.md)
