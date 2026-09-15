# Backlog: agent-image provisioning & daemon observability

> **ARCHIVED — superseded.** Part of the phase-1 planning corpus (July 2026), written before the
> agent platform existed. Kept for provenance only. The active corpus is
> [`docs/phase-2/`](../../phase-2/); the live backlog is
> [`Mainguard_Backlog.md`](../../planning/Mainguard_Backlog.md).

*Written 2026-07-17, at the close of the update-mechanism work (#193–#201 + the tier-2 VM upgrade).
These are the two structural gaps that session surfaced and deliberately did not fix. Both were
found the hard way — the field evidence below is from a real install, not speculation.*

---

## 1. `mainguard-agent-base` never reaches installed VMs

### Status: ADDRESSED (2026-07-18)

Shipped in two parts. **v1** (`8935e07`) added provisioning-time in-VM `docker build` over `IWslRunner`,
wired at startup (`SandboxImageAutoProvision`), with a daemon spawn-preflight **presence** check — the
first three acceptance criteria (zero manual docker on a fresh OOBE, a working image in the upgraded
VM, egress-proxy rides the same mechanism). The deferred fourth criterion — **image version labels /
staleness detection** — is now complete (0.2.4 lockstep):

- Each image is stamped `mainguard.image.version=<source-hash>` at build time; the expected hash is a
  committed Core constant (`SandboxImageVersions`), recomputed from the curated build inputs by
  `SandboxImageSourceHasher` and kept honest by `SandboxImageVersionsGuardTests`.
- **Detect → repair, two sites:** the app startup probe (`ProbeNeedsProvisionAsync`) reports each
  image Missing **or** Stale and (re)provisions it; the daemon spawn preflight
  (`ISandboxEngine.ImageVersionAsync`) returns an actionable `FailedPrecondition` naming a
  missing/outdated image. `Tools → Rebuild sandbox images` is the manual force-reprovision recovery.
- **Approach B (primary) + A (fallback):** provisioning prefers a `docker load` of the app-bundled,
  CI-built `payload/images/<name>.tar` (CI bytes = VM bytes, offline, the label rides the tar) and
  falls back to a labelled in-VM `docker build`. CI produces + uploads the tars; the App bundles them
  warn-if-missing (`$(MainguardImageTars)`).

Versioning discipline: an image-source change ⇒ a new hash constant ⇒ a lockstep App/Server bump (the
daemon preflight compares it), but **no `build/mainguardos/VERSION` cut** (images ship beside the app).
See AGENTS.md §"Sandbox image versioning convention" and `docs/mainguardos-updates.md`.

**Future (not blocking):** an in-VM behavioral test of the stale→rebuild→toast loop against a real VM,
and a richer inline build-progress surface on the loading screen (the current design kicks the build
in the *background* after the loading screen proceeds, so per-step progress lands in `oobe.log`).

### The gap

The hardened jail image every agent container runs (`images/mainguard-agent-base`, P2-07) is built in
CI (`docker build -t mainguard-agent-base:latest images/mainguard-agent-base` in `ci.yml`) and by the
release pipeline — **and nowhere else**. Nothing in the OOBE, the MainguardOS payload, or any
provisioning step ever loads it into the VM's dockerd. A freshly imported `MainguardEnv` has an empty
image store, so the **first agent spawn on every real install fails**.

### Field evidence

On a provisioned install with a healthy, current daemon, "Start coordinator" crashed the
`SpawnAgent` handler: the launcher took the real-jail path and Docker threw
`No such image: mainguard-agent-base:latest`. Before the error-mapping fix (#201) this surfaced as a
bare `Unknown — Exception was thrown by handler`; it now surfaces as an actionable
`FailedPrecondition` naming the image — but the spawn still fails. The interim unblock was a manual
in-VM build (`wsl -d MainguardEnv -- docker build -t mainguard-agent-base:latest <repo>/images/mainguard-agent-base`).

### Constraints on any fix

- **G-16**: no `docker build` at *agent-runtime* (a runtime build severs the agent PTY). A
  provisioning-time build (OOBE, upgrade, or an explicit repair action) does not violate this.
- The image must track its source: a stale image is the same skew class the daemon had before
  tier-1 — whatever ships needs a version/label the daemon can check at spawn preflight.
- The egress-proxy image (`images/mainguard-egress-proxy`) has the same problem and should ride the
  same mechanism.

### Candidate approaches

| Approach | How | Trade-offs |
|---|---|---|
| **A. OOBE/upgrade build step** | A provisioning step (alongside `StartDaemonStep`) runs `docker build` inside the VM from a source tree bundled with the app (or baked into the payload at `/opt/mainguard/images/`). | No huge artifact to ship; needs network at setup time (apt/Nix fetches inside the Dockerfile); minutes-long, needs progress UI; build inputs must be pinned for reproducibility (the Dockerfile already pins). |
| **B. Bundled image tar** | CI `docker save`s the built image; the payload (or the app's `payload/` dir) carries `mainguard-agent-base.tar`; a provisioning step `docker load`s it. | Fully offline + deterministic (the CI-built bytes are what runs); large artifact (hundreds of MB — likely rules out embedding in the MainguardOS tarball without breaking its size/hash discipline; ship beside it like the daemon payload); trivially versionable via a manifest label. |
| **C. Registry pull** | Publish to a registry (GHCR); the VM pulls at provisioning/first-spawn. | Simplest pipeline; requires network + a public registry story + supply-chain verification (digest pinning); first-spawn pulls re-introduce a long silent wait unless surfaced. |

**Leaning**: B for correctness (CI bytes = runtime bytes, offline installs work), delivered through
the same packaging seam tier-1 uses (`$(MainguardDaemonPayload)`-style optional MSBuild copy +
warn-if-missing), loaded by a new provisioning step that both the OOBE and the tier-2 upgrade run,
with a daemon-side spawn preflight that checks image presence + version label and returns the
(now-actionable) `FailedPrecondition` naming the repair when absent.

### Acceptance criteria

- A fresh OOBE install spawns its first agent with **zero manual docker commands**.
- The tier-2 VM upgrade leaves a working image in the new distro (rebuilt/reloaded, not assumed
  migrated — the image store lives outside `/home/mainguard` and is *not* covered by the user-data
  migration).
- A version-skewed image is detected at spawn preflight and surfaced actionably (same honesty bar
  as #201), ideally auto-repaired by the same mechanism.
- `mainguard-egress-proxy` ships the same way.

---

## 2. The daemon is observably silent

### Status: ADDRESSED (2026-07-18)

Shipped as **in-depth, categorized daemon logging** (`feat(daemon): in-depth per-subsystem logging`).
One pipeline, two sinks, registered in `DaemonHost` when `!options.Smoke`: a single-line journald
console (`journalctl -u mainguardd`) **and** per-subsystem rolling files under `~/.mainguard/logs/<subsystem>.log`
(`SubsystemFileLoggerProvider`, 5 MB × 3, per-line flush). Twelve categories (`DaemonLogSubsystems`):
lifecycle · migration · rpc · spawn · egress · gateway · terminal · merge · approval · killswitch ·
coordinator · intake. The `SecretMaskingInterceptor` now records handler faults (non-`RpcException` →
Error under `Rpc` with method/type/stack, then rethrow), the #194 migration path logs its milestones
("preparing db / stale migration lock cleared / migrate ok / **migrate watchdog fired**"), and the App
exposes a read-only **Settings → Daemon logs** panel over Core's `DaemonLogReader`. Mask discipline is
kept green (`LoggingMaskTests` + the `// SECRET`-coverage test). See the AGENTS.md "Daemon logging
(mainguardd) convention" subsection and `docs/mainguardos-updates.md` §"Versioning discipline".

**Deferred (future, noted below and in the acceptance criteria):** first-class `MAINGUARD_SUBSYSTEM=`
journald fields via libsystemd interop, a JSON/structured formatter (P2-16 SIEM export is the natural
home), live in-app log streaming / a rich log-viewer UI (P2-41), and file-based rotation tuning. The
per-category **files** are the primary "different logs" surface until those land.

### The gap

`mainguardd` produces **no log output at all** — not to the journal, not to a file. ASP.NET Core's
default console logging does not survive the daemon's host setup, there is no logging pipeline of
its own, and unhandled RPC handler exceptions vanish (gRPC swallows them into
`Unknown — Exception was thrown by handler` with nothing recorded daemon-side).

### Field evidence (all from one evening)

- The EF migration-lock hang (#194): the daemon sat "active" for hours doing literally nothing;
  `journalctl -u mainguardd` showed only systemd start/stop lines. Diagnosis required a `createdump`
  of the live process and cross-OS `dotnet-dump` analysis — for what one log line
  ("acquiring migration lock…") would have made obvious.
- The missing-image spawn crash (#201): the handler exception was recorded **nowhere**. The only
  trace was the client's `Unknown`.
- Every skew/connectivity incident that evening was diagnosed from *outside* the daemon (ss, proc,
  binary greps) because the daemon could not tell its own story.

### What exists already (client-side, not daemon-side)

The App logs its half to `oobe.log` (`LogOobe`), and #199/#201 made client-facing errors carry the
daemon's `Status.Detail`. The daemon side is still a black box; #201's catch-all mapping is a
mitigation, not observability.

### Proposed shape

- **Journal-first**: systemd already captures stdout/stderr — wire standard
  `Microsoft.Extensions.Logging` console output (single-line, no color codes) so
  `journalctl -u mainguardd` becomes the one place to look. No custom sink, no file rotation to own.
- **Severity discipline**: startup milestones (DB prepared / migration lock cleared / bound
  `127.0.0.1:5250`), every RPC-handler exception (interceptor-level, with the method name), spawn
  chain steps at Information; noisy per-frame paths (terminal streaming, event fan-out) stay silent
  or Debug-gated.
- **Secrets**: the existing `LoggingMaskTests` mask discipline applies — `// SECRET` fields
  (model keys, tokens) must never reach a log line; extend the mask tests over the new pipeline.
- **A diagnostics affordance**: `HealthCheckStep`/`IDaemonHealthDiagnostics` already read the
  journal tail for OOBE failures — once the daemon actually writes there, that same tail becomes
  useful in the Settings/About surface ("last daemon log lines") for bug reports.

### Acceptance criteria

- `journalctl -u mainguardd` shows startup milestones, the bound endpoint, and every handler
  exception with method + stack — on the stock payload, with no extra configuration.
- The #194 hang scenario, replayed, is diagnosable from the journal alone in under a minute.
- No secret value ever appears in any log line (mask tests extended to the new pipeline).
- Log volume at idle is ~zero (no per-frame chatter).

---

## Sequencing note

Item 2 is small, self-contained, and multiplies the value of every future incident — it should go
first. Item 1 rides the packaging + provisioning seams tier-1/tier-2 just established and closes
the last "fresh install can't actually run an agent" gap; its daemon-side preflight half depends on
nothing and could land independently of the packaging half.

*Related, smaller follow-ups already tracked elsewhere: packaged-build `/p:MainguardDaemonPayload`
wiring, an App/Server version-lockstep CI guard (the binding version-bump rules the guard would
enforce are documented in `docs/mainguardos-updates.md` §"Versioning discipline"), a manual-rollback
surface for the tier-1 `RestoreRollback` builder, the tier-2 manual-verification runbook (in
PR #202's description), a disk-headroom preflight for the tier-2 upgrade, and the AGENTS.md
duplicated-map-block cleanup.*
