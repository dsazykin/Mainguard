# P2-05 — `MainguardOS` Bootstrapper — Implementation Plan

**Task ID:** P2-05 · **Milestone:** M6 · **Priority:** P0
**Depends on:** P2-02 (daemon to launch); the installer payload that ships the distro tarball
arrives with P2-21 — this task consumes a versioned tarball path and must work from a locally
provided one.
**Branch:** implement on `feature/P2-05-mainguardos-bootstrapper` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated pure units (INI merger, state machine) + **required manual WSL matrix — human approval required**.
> Check/act seams are mocked for CI, but real-WSL behavior (fresh import < 60 s, `docker info` green, kill-VM resume, other distros untouched, `ptrace_scope >= 2` after boot) cannot run in the PR gate (`RequiresWsl`) — a human executes the matrix on a real machine and pastes evidence into the PR.
>
> **Source of truth:** §P2-05 of `docs/phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.2a). Global invariant **G-12: never `wsl --shutdown`** — lifecycle is
> `--terminate MainguardEnv` → poll → `--unregister`.

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
| [Master doc](../phase-2/implementation_plans/Mainguard_Master_Implementation_Document_v2.md) §P2-05 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/Mainguard_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/Mainguard_Test_Implementation_Strategy_v2.md) **TI-P2-05** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-05 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Launch-blocker / hardening gates** | **P2-07 G2 control (2), boot-provisioned here:** `kernel.yama.ptrace_scope=2` is a non-namespaced sysctl that MUST be set VM-wide at first boot by this bootstrapper (it cannot be a per-container flag) -- see the G2 addition in §Implementation additions below |

---

## 0. Context — what exists today

Nothing WSL-related exists in the codebase. Agents run in Docker containers inside a dedicated
WSL2 distro (**`MainguardEnv`**) so the user's own distros and Docker Desktop are never touched.
This task ships the client-side bootstrapper that gets a cold Windows machine (WSL2 already
enabled — enablement is P2-21's job) to a running `mainguardd` health-checked over gRPC.

### What you can rely on

| Fact | Where |
|---|---|
| Daemon binary + gRPC health surface + token file | P2-02 (`Mainguard.Server`, `DaemonClient`) |
| Typed exceptions; no bare throws | `Mainguard.Agents/Exceptions/` |
| Settings/first-run UI patterns | `Mainguard.App.Shell/ViewModels/` |
| Hardened process runner patterns (arg lists, no shell, output capture) — mirror the `RunGit` family's shape for a `RunWsl` helper | `Mainguard.Agents/Services/GitServices.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `Mainguard.Agents/Agents/Bootstrap/MainguardOsBootstrapper.cs` (state machine) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/IBootstrapStep.cs` + step classes (`DetectDistroStep`, `ImportDistroStep`, `WslConfigMergeStep`, `FirstBootStep`, `StartDaemonStep`, `HealthCheckStep`) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/WslRunner.cs` (hardened `wsl.exe` invocation — arg lists, UTF-16 output handling, no shell) |
| **Create** | `Mainguard.Agents/Agents/Bootstrap/WslConfigMerger.cs` (pure INI merge) |
| **Create** | `Mainguard.App.Shell/ViewModels/BootstrapProgressViewModel.cs` + `Mainguard.App.Shell/Views/BootstrapProgressView.axaml(.cs)` (staged checklist UI) |
| **Create** | `Mainguard.Tests/WslConfigMergerTests.cs`, `BootstrapStateMachineTests.cs` |
| **Edit** | `docs/repo-map/` index |

---

## 2. Contract (binding, from strategy §G-7.2a)

`MainguardOsBootstrapper` (client-side), responsibilities in order:

1. **Detect** `MainguardEnv` via `wsl.exe --list --quiet` (mind UTF-16LE output of `wsl.exe`).
2. **Import** from the versioned tarball if absent: `wsl --import MainguardEnv <installDir> <tarball> --version 2`.
3. **Merge, never clobber** `%UserProfile%\.wslconfig`: INI parse, add only our keys under
   `[wsl2]`, back the file up first. Defaults: `memory = min(50% RAM, 8GB)`,
   `autoMemoryReclaim=gradual`.
4. **First boot:** raise `fs.inotify.max_user_watches`; **set `kernel.yama.ptrace_scope=2`
   VM-wide** (the non-namespaced sysctl the P2-07 G2 quartet's control (2) depends on — via
   `/etc/sysctl.d/` or the `/etc/wsl.conf` boot command; **it cannot be a per-container flag**,
   Docker permits only namespaced sysctls); `dockerd` starts via an `/etc/wsl.conf`
   `[boot] command=`; wait for the Docker socket.
5. **Launch `mainguardd`**, health-check its gRPC endpoint; drive a staged-checklist progress UI.
6. **Idempotent:** every step checks-then-acts; a partially bootstrapped machine resumes.

---

## 3. Implementation steps

### 3.1 `WslRunner`

`Run(params string[] args)` → `ProcessStartInfo` with an **argument list** (never a concatenated
string, never `cmd.exe`), `StandardOutputEncoding = Encoding.Unicode` for `wsl.exe` (its output is
UTF-16LE; `--list --quiet` parsing breaks otherwise — normalize NULs/BOM defensively). Capture
stdout/stderr, timeout, typed `MainguardException` subtypes on nonzero exit. In-distro commands go
through `wsl -d MainguardEnv -- <cmd> <args...>`.

### 3.2 `WslConfigMerger` (pure — the fixture-tested heart)

```csharp
public static class WslConfigMerger
{
    /// <summary>Returns the merged .wslconfig content. Only adds/updates the provided keys under
    /// [wsl2]; every other section, key, comment, and blank line is preserved verbatim.</summary>
    public static string Merge(string? existingContent, IReadOnlyDictionary<string, string> wsl2Keys);
}
```

- Line-based INI handling that **preserves unknown content byte-for-byte** (comments `#`/`;`,
  unknown sections, user's key order). Our keys: update in place if present under `[wsl2]`
  (**only if unset — an existing user value wins**; see edge row 1), else append under the
  section, creating `[wsl2]` at the end if absent.
- The bootstrapper computes `memory` from `GlobalMemoryStatusEx`-equivalent
  (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` is acceptable) → `min(50% RAM, 8GB)`,
  formatted like `6GB`.
- Caller writes `%UserProfile%\.wslconfig.mainguard.bak` (timestamped) **before** writing the merge
  result.

### 3.3 State machine

```csharp
public interface IBootstrapStep
{
    string Name { get; }                       // UI checklist label
    Task<bool> IsSatisfiedAsync(CancellationToken ct);   // check
    Task ExecuteAsync(IProgress<string> log, CancellationToken ct);  // act
}
```

`MainguardOsBootstrapper.RunAsync` iterates the ordered steps: skip when satisfied (idempotency),
execute otherwise, re-verify after execute. Each step's check/act pair is seam-mocked in unit
tests (inject `IWslRunner`, `IFileSystem`-thin wrappers). Failure → typed exception carrying the
step name; the UI renders the failed stage with the actionable message. **WSL not installed** →
actionable failure telling the user to run the installer's enablement flow (P2-21 owns
enablement — do not attempt `wsl --install` here).

Step specifics:

- **FirstBootStep:** `wsl -d MainguardEnv -- sysctl -w fs.inotify.max_user_watches=524288` **and**
  `sysctl -w kernel.yama.ptrace_scope=2` (G2 control 2 — VM-wide, boot-provisioned here because
  it is non-namespaced and unreachable from `CreateContainerAsync`) + persist **both** to
  `/etc/sysctl.d/`; verify `/etc/wsl.conf` contains the `[boot]` dockerd command (the tarball
  ships it; repair if missing); poll `docker info` inside the distro until green (timeout →
  typed failure with the tail of dockerd logs). The step's check phase asserts
  `kernel.yama.ptrace_scope ≥ 2` — a VM where it regressed re-provisions, and P2-07's key-custody
  guarantee names this check as its dependency.
- **StartDaemonStep:** launch `mainguardd` inside the distro (systemd unit if the tarball enables
  systemd, else nohup via the boot command); **HealthCheckStep** uses `DaemonClient` against the
  daemon endpoint until healthy (bounded retries).
- **Terminate mid-bootstrap resume:** because every step re-checks, `wsl --terminate MainguardEnv`
  at any point → next `RunAsync` resumes where reality left off (edge row 2, required test).

### 3.4 Progress UI

`BootstrapProgressViewModel`: `ObservableCollection<BootstrapStageViewModel>` (name, state:
pending/running/done/failed, log tail). Stages map 1:1 to steps. Runs off the UI thread; cancel
supported between steps. Design tokens + component classes only.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| existing user `.wslconfig` keys | preserved verbatim (fixture-tested INI merger; user's `memory=` wins over ours) |
| `wsl --terminate` mid-bootstrap | next start resumes; no duplicate import, no config double-append |
| WSL not installed | actionable typed failure (points at installer enablement; P2-21 owns it) |
| tarball path missing/corrupt | typed failure naming the path; nothing half-imported (verify `--import` exit code; unregister a failed partial import before retry) |
| re-run on a healthy machine | full no-op (every `IsSatisfiedAsync` true; zero mutations) |

---

## 5. Invariants (MUST)

1. **Never `wsl --shutdown`** (G-12) — lifecycle verbs are `--terminate MainguardEnv` → poll →
   `--unregister MainguardEnv`, and only against our distro.
2. Re-run is a no-op.
3. Other distros untouched (uninstall/lifecycle tests assert `wsl --list` before/after ==
   modulo `MainguardEnv`).
4. `.wslconfig` merge never loses user content; backup written before every write.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `WslConfigMerger_Fixtures` | fixture set: empty file / no `[wsl2]` / existing `[wsl2]` with other keys / our keys already set (user value preserved) / comments+unknown sections / CRLF — output matches committed expected files |
| 2 | `Merger_IsPure_NoIO` | merge of same inputs is deterministic; class has no filesystem access |
| 3 | `StateMachine_SkipsSatisfiedSteps` | all-satisfied steps → zero `ExecuteAsync` calls |
| 4 | `StateMachine_ResumesAfterFailure` | step 3 fails → rerun executes only steps 3+ (1–2 satisfied) |
| 5 | `StateMachine_FailureCarriesStepName` | typed exception names the failing stage |
| 6 | `WslRunner_Utf16ListParsing` | recorded UTF-16LE `--list --quiet` output parses distro names correctly |
| 7 | `NoShutdownAnywhere` | source grep test (or analyzer) — the literal `--shutdown` absent from Core/Server/installer |
| 8 | `FirstBootStep_ShouldProvisionPtraceScope2` (**G2 control 2**) | the step's command list includes `kernel.yama.ptrace_scope=2` + `/etc/sysctl.d/` persistence; the check phase asserts current value ≥ 2 (seam-mocked runner) |

**Manual matrix pasted into the PR** (cannot be CI'd): fresh import < 60 s on the dev machine;
`docker info` green inside the VM; **`wsl -d MainguardEnv -- sysctl kernel.yama.ptrace_scope`
prints ≥ 2 after boot** (the P2-07 quartet's boot-provisioned control — paste the output);
kill-VM (`wsl --terminate`) mid-bootstrap → resume completes; `wsl --list` before/after shows
other distros untouched.

---

## 7. Rejection triggers / Reviewer script

**Rejection:** any `--shutdown`; clobbering `.wslconfig` (write without merge/backup); shell-string
process invocation; enablement logic (`wsl --install`) creeping in from P2-21's scope.

```bash
dotnet build Mainguard.slnx
dotnet test --filter "FullyQualifiedName~WslConfigMerger|FullyQualifiedName~BootstrapStateMachine"
grep -rn -- "--shutdown" Mainguard.Agents/ Mainguard.Server/ installer/ 2>/dev/null   # 0 hits (G-12)
grep -rn "cmd.exe\|/bin/sh -c" Mainguard.Agents/Agents/Bootstrap/                   # 0 hits
```

---

## 8. Definition of done

- [ ] `WslRunner`, pure `WslConfigMerger` (+ fixtures), step-based `MainguardOsBootstrapper`, staged progress UI.
- [ ] `kernel.yama.ptrace_scope=2` provisioned VM-wide at first boot + persisted + check-phase asserted (G2 control 2 — P2-07 depends on it).
- [ ] Idempotent end-to-end: fresh import, resume, healthy no-op — manual matrix in the PR description (incl. the ptrace_scope output).
- [ ] All edge rows + invariants tested; G-12 grep clean; test contract = union of §6 and TI-P2-05.
- [ ] `docs/repo-map/` index updated. One task = one PR linking **P2-05**, base `phase2`.
