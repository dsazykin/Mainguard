# P2-21 — Installer Part 1: Diagnostics, OS Enablement, Payload Pipeline — Implementation Plan

**Task ID:** P2-21 · **Milestone:** M7 · **Priority:** P0 for distribution.
**Depends on:** P2-05 (shares bootstrap code).
**Branch:** implement on `feature/P2-21-installer-diagnostics` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated parsers/state machines + CI tarball build + **required manual install matrix — human approval required**.
> WSL-status parsing, OOBE state machine, and tarball hash-stability are CI. Fresh install, UAC-at-Construct-Sandbox-only, reboot-resume, and VM snapshot upgrade can only be verified by a human on real Windows (`RequiresWsl` manual matrix) — evidence pasted into the PR.
>
> **Source of truth:** §P2-21 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §§J-1–J-3). G-12 applies to every lifecycle path here.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-21 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-21** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-21 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-05's bootstrapper assumes WSL2 is enabled and a tarball exists. This task builds the road to
that point: preflight diagnostics, the unelevated OOBE that enables Windows features with exactly
one UAC prompt, reboot-resume, and the reproducible `MainguardOS.tar.gz` build pipeline with an
upgrade path.

### What you can rely on

| Fact | Where |
|---|---|
| `MainguardOsBootstrapper` steps + `WslRunner` (UTF-16 parsing) + INI merger | P2-05 |
| Staged-checklist progress UI pattern | P2-05 `BootstrapProgressViewModel` |
| CI (GitHub Actions) for the payload build | `.github/workflows/` |

New surface: an `installer/` tree (OOBE app + elevated helper) — likely a separate slim exe
(`Mainguard.Installer`) sharing `Mainguard.Agents/Agents/Bootstrap` code.

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `installer/Mainguard.Installer/` (unelevated OOBE app; Avalonia, reuses design tokens) |
| **Create** | `installer/Mainguard.Installer.Elevated/` (tiny elevated helper: feature enablement only) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/SystemDiagnostics.cs` (+ `DiagnosticCheck` model) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/WslStatusParser.cs` (pure; per-WSL-version fixtures) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/OobeStateMachine.cs` + `oobe-state.json` schema |
| **Create** | `installer/resume/` — elevated Scheduled Task registration for reboot-resume (never `RunOnce`) |
| **Create** | `build/mainguardos/` (reproducible tarball build: Dockerfile/mkosi + pinned inputs + `/etc/mainguardos-release` stamp) + CI job |
| **Create** | `docs/mainguardos-updates.md` (CVE patch cadence) |
| **Create** | `Mainguard.Tests/WslStatusParserTests.cs`, `SystemDiagnosticsTests.cs`, `OobeStateMachineTests.cs`, VM upgrade test script |
| **Edit** | `Mainguard.slnx` (installer projects), `docs/repo-map/` index |

---

## 2. Contract (binding)

- **`SystemDiagnostics`:** Win11 x64 build check, WMI virtualization flags
  (`Win32_ComputerSystem.HypervisorPresent` / firmware VT-x), WSL2 state parse, ≥ 20 GB free
  disk. Each check returns `Pass | Fail(actionable message + doc link)`. **Hard-stop before any
  system modification** on failure. ARM64 → explicit unsupported gate.
- **OOBE:** runs unelevated; UAC prompt only at the single "Construct Sandbox" step (elevated
  helper relaunch); `Enable-WindowsOptionalFeature` (`Microsoft-Windows-Subsystem-Linux`,
  `VirtualMachinePlatform`) with the raw PowerShell surfaced to the user; reboot-resume via an
  **elevated Scheduled Task** (never `RunOnce`) + `oobe-state.json`.
- **Payload pipeline:** reproducible `MainguardOS.tar.gz` build in `build/mainguardos/` (versioned
  `/etc/mainguardos-release`); silent import reusing P2-05; **in-place VM upgrade** preserving
  provisioned repos; documented CVE patch cadence.

---

## 3. Implementation steps

1. **`WslStatusParser` (pure):** parse `wsl --status` / `wsl --version` / feature-state outputs —
   fixtures captured per WSL version (store raw UTF-16LE bytes; the parser takes decoded strings
   from `WslRunner`). Distinguish: not installed / installed WSL1-only / WSL2 ready / needs
   kernel update.
2. **`SystemDiagnostics`:** ordered checks, each independent, aggregated report; every `Fail`
   carries the fix action text + docs URL. ARM64 detection (`RuntimeInformation.OSArchitecture`)
   → unsupported gate before anything else.
3. **OOBE state machine:** states `Diagnostics → EnableFeatures → RebootPending → Resumed →
   ImportVm (P2-05) → Done`, persisted to `oobe-state.json` (appdata) after each transition;
   idempotent resume (reuse the P2-05 check-then-act pattern).
4. **Elevated helper:** separate signed exe doing exactly: enable the two features + register the
   resume Scheduled Task (elevated, `ONLOGON`, self-deleting after resume completes). The raw
   PowerShell it runs is displayed in the OOBE before the UAC prompt. Helper communicates result
   via exit code + a result file; no other privileged work ever moves into it.
5. **Payload build:** containerized rootfs build (pinned base snapshot + package list with
   versions, dockerd + boot config from P2-05's expectations, `/etc/mainguardos-release` with
   version + build inputs hash). CI job builds + records the tarball hash — **hash-stable given
   pinned inputs** (invariant; verify by double-build in CI).
6. **Upgrade path:** vN→vN+1 = import new distro alongside (`MainguardEnv-staging`), migrate
   `~/mainguard` (repos/worktrees) via tar stream or rename within the VHDX strategy chosen,
   swap names, unregister old — scripted + automated test on VM snapshots; **provisioned repos
   preserved** (invariant test).
7. **`docs/mainguardos-updates.md`:** cadence (monthly + critical CVE out-of-band), what gets
   patched (base packages, kernel deferred to WSL), how upgrades reach users (payload version
   check on app update).

---

## 4. Edge-case matrix (binding — each row needs a test where automatable)

| Case | Required behavior |
|---|---|
| virtualization disabled in firmware | diagnostics `Fail` with BIOS instructions + doc link; nothing modified |
| ARM64 machine | explicit unsupported gate at entry |
| reboot interrupts enablement | Scheduled Task resumes OOBE at `Resumed`; state file consistent |
| resume task runs twice | idempotent (state machine no-ops completed steps) |
| disk < 20 GB | hard-stop before modification |
| upgrade vN→vN+1 | repos/worktrees intact post-swap |

---

## 5. Invariants (MUST)

1. WSL-status parsers fixture-tested against captured outputs per WSL version.
2. Tarball hash-stable given pinned inputs.
3. vN→vN+1 upgrade preserves repos/worktrees (automated test).
4. Exactly one UAC prompt in the happy path; the elevated helper's scope never grows.
5. G-12: no `--shutdown` anywhere in installer code.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `WslStatusParser_VersionMatrix` | fixtures (no WSL, WSL1, WSL2 ok, kernel outdated) → correct states |
| 2 | `Diagnostics_FailuresActionable` | each check's fail carries message + link; hard-stop flag set |
| 3 | `Oobe_StateMachine_ResumeMatrix` | interrupt at each state → resume completes remaining steps only |
| 4 | `Oobe_StateFile_SchemaRoundTrip` | serialize/deserialize stable; unknown fields tolerated |
| 5 | CI `Tarball_Reproducible` | build twice → identical hash |
| 6 | `Upgrade_PreservesRepos` (VM-scripted) | seeded repos/worktrees present after upgrade; old distro unregistered |
| 7 | Manual matrix in PR | fresh Win11 VM snapshot: diagnostics → enable → reboot → resume → import → daemon healthy; screenshots + timings |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** `RunOnce` for resume; privileged work beyond the two enumerated actions in the
elevated helper; system modification before diagnostics pass; unpinned payload inputs.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~WslStatusParser|FullyQualifiedName~SystemDiagnostics|FullyQualifiedName~Oobe"
grep -rn "RunOnce" installer/            # 0 hits
grep -rn -- "--shutdown" installer/      # 0 hits (G-12)
```

---

## 8. Definition of done

- [ ] Diagnostics (hard-stop, actionable, ARM64 gate) + fixture-tested parsers.
- [ ] Unelevated OOBE + single-UAC elevated helper + Scheduled-Task reboot-resume + state file.
- [ ] Reproducible tarball CI build + upgrade path test + `docs/mainguardos-updates.md`.
- [ ] Manual VM matrix evidenced in the PR. `docs/repo-map/` index updated. One task = one PR linking **P2-21**, base `phase2`.
