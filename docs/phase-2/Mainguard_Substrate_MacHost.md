# Mainguard — macOS Host Substrate (the `SubstrateId = "macos-host"` implementation)

Status: **shipped with the phase2 macOS port** · Owner doc: [`Mainguard_Environment_Substrate_Contract.md`](Mainguard_Environment_Substrate_Contract.md) (the ESC) · Decision record: `docs/phase-2/ADRs.md` ADR-008

## Contents

- §0 Scope — what this substrate is, and how it deviates from the ESC's deferred B4 sketch
- §1 Topology + lifecycle — the macos-host realization
- §2 Engine variability — Docker Desktop / OrbStack / Colima
- §3 Cold-start / latency budgets — the filled ESC §5.3 template
- §4 Conformance results — ESC §4 on this substrate
- §5 Known limits and follow-ups

---

# 0. Scope

This document is the per-platform realization the ESC §0.3 requires for macOS: one conforming
`IAgentEnvironment` (`Mainguard.Agents/Agents/MacHostAgentEnvironment.cs`), its §4 conformance
results, and its §5 metric table. It deviates deliberately from the ESC's deferred **B4
"macos-vm"** sketch (daemon inside a Mainguard-owned Linux VM): on macOS every Docker engine
already IS a managed Linux VM with host file sharing, so this substrate runs **mainguardd
natively on the Mac** and treats the engine's VM as the sandbox host. ADR-008 records the
deviation and its compensating controls; B4 stays deferred and unclaimed.

# 1. Topology + lifecycle

| ESC §2 slot | macos-host realization |
|---|---|
| SUBSTRATE boundary | the machine's Docker engine (its own Linux VM); the daemon is a native osx-arm64 process |
| Daemon home | `~/.mainguard` (data root), `~/mainguard` (substrate root: `repos/`, `worktrees/`, `agents/`, `caches/`, `adapters/`, `toolchains/`) |
| `SyncRemote` | name `mainguard-local`, URL = the local bare path `~/mainguard/repos/<hash>.git` (daemon and client are the same machine) |
| host↔substrate object transport | none needed host-side (one filesystem); container-side is the engine's file sharing (virtiofs) |
| `Capabilities` | `(SupportsMaxIsolationBackend: false, SupportsWarmPoolPrestart: false, FilesystemTransport: "virtiofs", LifecycleDialect: "docker")` |
| Lifecycle | no VM of ours to import/terminate/unregister — the engine manages its own; the local daemon is started from the app payload by `MacDaemonController` (dotnet muxer, pgrep discovery, SIGTERM→SIGKILL stop) and tier-1-refreshed by `MacDaemonUpdater` (stop + restart from the payload) |
| Docker endpoint | `DockerEndpointResolver`: `DOCKER_HOST` → the docker CLI's current context (read from `~/.docker`, no process spawned) → well-known engine sockets → library default. Engine-agnostic by requirement. |
| CLI / toolchain installs | `ContainerAdapterInstallHost`: a disposable agent-base container with `~/mainguard/adapters` and `~/mainguard/toolchains` mounted read-write **at their VM paths**, so command shapes, markers and the VmRoot→SandboxMount rewrite stay substrate-invariant; jails mount the same trees read-only |
| ESC-I1 | **structural**: the substrate declares `AllowedMountRoots` (`~/mainguard`, the data root) and `ContainerSpecBuilder` refuses any bind source outside them at spec construction |
| Startup (Pro app) | `MacStartupEnvironment` behind the same `AppStartupSequence`: wake = ensure local daemon; tier-2 VM upgrade never offers; sandbox-image probe/build over `HostCommandRunner` (the in-distro command interpreter for a host with native docker) |
| OOBE | none yet — the launch routes straight to the control center; a Docker-engine detection/file-sharing-canary OOBE is a follow-up (§5) |

# 2. Engine variability

The substrate binds to no engine. Measured on OrbStack (dev machine); the resolver and the
posture notes below are the engine matrix:

| Axis | Docker Desktop | OrbStack | Colima |
|---|---|---|---|
| Socket | `~/.docker/run/docker.sock` (or the `/var/run` symlink) | CLI context `orbstack` → `~/.orbstack/run/docker.sock` | `~/.colima/default/docker.sock` |
| File sharing | virtiofs, settings-scoped dirs (defaults cover `$HOME`) | virtiofs, whole-host | sshfs/virtiofs per config |
| userns-remap | unsupported | inherit-only | engine-config |
| Arch | arm64 native (+Rosetta) | arm64 native (+Rosetta) | arm64 native |

Jail images build **natively arm64** (TARGETARCH-parametrized Dockerfiles, per-arch
publisher-verified pins) — no Rosetta dependency, which keeps Colima viable.

# 3. Cold-start / latency budgets (ESC §5.3)

> Substrate: `macos-host` · RttBudget: local (loopback daemon, same-machine engine) ·
> Fixture: small repo (2 files, 1 commit) · Machine: Apple Silicon (arm64), OrbStack, 2026-08-18.
> Measured through the daemon RPCs (`[H1]` prints; ADR-007 discipline — printed, not asserted).

| Metric | Cold | Warm |
|---|---|---|
| Provision time | 288 ms (bare clone) | 146 ms (incremental fetch) |
| Worktree-create time | included in spawn below | — |
| Spawn → RPC returns (jail created, egress attached, CLI exec'd) | 1 554 ms | — (persistent-jail reuse not yet measured) |
| Stop (credential harvest + teardown) | 948 ms | — |
| First-byte PTY latency | not yet measured (needs the terminal-stream tap) | — |
| Mount/FS read latency (virtiofs) | not yet measured (in-sandbox microbenchmark pending) | — |
| Substrate cold start | N/A — no Mainguard-owned VM; the engine's own start is outside our lifecycle | near-zero (daemon start ≈ 3 s to bound + mTLS ready) |

# 4. Conformance results (ESC §4)

Run on-device, 2026-08-18, `port/macos`:

| # | ESC §4 row | Where it is proven on this substrate | Result |
|---|---|---|---|
| 1 | `GitObjectsRoundTrip_ShouldBeByteIdentical` | `SubstrateConformanceTests` (host fetch through the opaque `mainguard-local` handle); the in-jail-commit leg is `MirrorReadOnlyDockerTests` | **green** |
| 2 | `NoHostPathMount_ShouldHoldForEveryContainer` | `SubstrateConformanceTests` (live-jail bind-source sweep) + `ContainerSpecMountRootsTests` (the structural `AllowedMountRoots` refusal) | **green** |
| 3 | `SandboxRemotes_ShouldBeExactlyQuarantine` | `MirrorReadOnlyDockerTests` + the A6 `DaemonGitProxyDockerTests` | **green** |
| 4 | `TeardownNoResidue_AndHostUntouched` | deferred — `TeardownAsync` is not yet on the facade (interface's own additive-growth note); worktree/repo teardown is exercised by the lifecycle Docker tests | partial |
| 5 | `HealthAndUpgrade_ShouldBeIdempotent` | deferred — same facade note; the tier-1 refresh path is `MacDaemonUpdater` | deferred |
| 6 | `ControlPlaneOnly_UiHasNoSubstrateHandle` | `EditionReferenceGraphTests` (CI reference-graph guard) | **green** |
| 7 | `NoRuntimeImageBuild_ShouldHold` | G-16 by construction; `SandboxImageProvisioner` is provisioning-time-only | **green** |
| 8 | `HardenedSpec_EveryFlagAsserted` | `JailRuntimePostureDockerTests` + the `ContainerSpec*` unit suites; the boot-sysctl leg is best-effort here (ADR-008 — the engine VM is not ours; Yama tolerated absent exactly as WSL2's FirstBootStep does) | **green** (sysctl leg substrate-aware) |
| 9 | `SecretChannelsOnly_NoArgvNoEnvFile` | `SecretDeliveryDockerTests` (per-owner 0400 tmpfs, stdin-only transport) | **green** |
| 10 | `WanLatency…` | N/A on this substrate's local RttBudget; owned by the P2-25 WAN job | n/a |
| 11 | `AuditPerAuthorityAction_ExactlyOne` | the per-touchpoint `AuditProbe` coverage (substrate-neutral) | **green** |

The full `RequiresDocker` tier — 106 tests — is green on this substrate (OrbStack, arm64), and a
real claude-code agent has been provisioned, spawned into a hardened jail, observed running, and
stopped with credential harvest through the daemon RPCs on-device.

# 5. Known limits and follow-ups

- **No macOS OOBE.** The launch routes straight to the control center; a Docker-engine
  detection + file-sharing canary + engine-sysctl bootstrap chain (the mac analogue of P2-05's
  steps) is the follow-up. Until then a missing engine surfaces through the startup sequence's
  daemon diagnosis, not a guided flow.
- **Gateway LAN bind.** MG-4's `auto` bind picks a LAN-reachable address so jails reach it
  through the engine NAT; follow-up: bind the engine bridge specifically (ADR-008).
- **Keyring.** The DataProtection master key is not DPAPI-wrapped off Windows; the Keychain
  (`ISecureKeyStore`) backend remains the planned hardening (`docs/planning` spec). Interim
  posture: 0600 files under the hardened data root.
- **Tools → Daemon logs page** still assumes journalctl; mac daemon logs live under
  `~/.mainguard/logs` and need a file-reader path.
- **Trash on discard, native toasts, tray polish, .dmg/notarized packaging** — deferred UI/
  distribution work, unchanged from the port plan.
