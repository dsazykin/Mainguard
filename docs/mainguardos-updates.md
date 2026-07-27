# MainguardOS payload — update & CVE patch cadence (P2-21)

How the `MainguardOS.tar.gz` payload (the WSL2 `MainguardEnv` root filesystem, built under
`build/mainguardos/`) is kept patched and how updates reach installed users. This is the documented
cadence P2-21 §3.7 requires; it governs deliberate bumps to the otherwise-pinned inputs that keep the
tarball hash-stable (invariant 2).

## What is in scope

| Layer | Patched here? | Notes |
|---|---|---|
| Base image (`debian:bookworm-slim@sha256:…`) | Yes | A *dated* `debian:bookworm-…-slim` pinned by digest in the `Dockerfile`; bumped to a newer dated digest on cadence (kept at or before `DEBIAN_SNAPSHOT`). |
| Debian snapshot (`DEBIAN_SNAPSHOT` in the `Dockerfile`) | Yes | The frozen `snapshot.debian.org` timestamp apt resolves against — moving it is how the package version floor advances. |
| APT packages (`packages.pinned.txt`) | Yes | A curated package **name** list; versions are fixed by the snapshot pin (not per-line). The docker/git/toolchain surface (now incl. `systemd`/`systemd-sysv`). |
| Mainguard daemon (`mainguardd` at `/opt/mainguard/`) | Yes — rebuilt each payload build | The published `Mainguard.Server` (linux-x64, self-contained), `COPY`'d into the image and supervised by the shipped-enabled `mainguardd.service`. It rides the app/source, not the apt snapshot: it advances whenever `Mainguard.Server` changes and a new payload `VERSION` is cut. Published **deterministically** (`Deterministic` + `ContinuousIntegrationBuild`, no ReadyToRun/single-file; deterministic portable PDBs ride along so the daemon logs' `ex.StackTrace` carries file:line) so it does not break the hash-stable invariant — the whole tarball stays byte-reproducible. |
| WSL2 Linux **kernel** | **No — deferred to WSL** | The kernel is Microsoft's WSL2 kernel, updated by `wsl --update`, not shipped in our rootfs. The OOBE/diagnostics surface a stale-kernel state (`WslInstallState.NeedsKernelUpdate`) but never bundle a kernel. |
| Agent-base container image | Separate pipeline | `images/mainguard-agent-base` (P2-07) has its own build + CVE flow; not part of the payload rootfs. |

## Cadence

- **Monthly baseline.** On the first week of each month, move `DEBIAN_SNAPSHOT` forward to a newer
  snapshot (picking up security-fixed package versions), bump the base-image digest to a dated
  `debian:bookworm-…-slim` at or before that snapshot, rebuild, and ship a new payload `VERSION`.
- **Out-of-band for critical CVEs.** A `Critical`/actively-exploited CVE in an in-scope package
  (docker, git, openssh, ca-certificates, the base libc) triggers an out-of-band snapshot bump within
  the security SLA — do not wait for the monthly train.
- **Deliberate pins only.** Every bump edits `DEBIAN_SNAPSHOT` (and, when needed, the `FROM` digest,
  or the package-name set in `packages.pinned.txt`) in the **same commit**, so the `build-inputs hash`
  in `/etc/mainguardos-release` changes intentionally and the reproducibility invariant still holds
  (pinned in → stable out). Floating the base tag or dropping the snapshot pin is a review rejection.

## How a bump is made

1. Edit `build/mainguardos/Dockerfile` — move `DEBIAN_SNAPSHOT` to a newer real snapshot timestamp
   (`YYYYMMDDTHHMMSSZ`), and bump the `FROM` digest to a dated `debian:bookworm-…-slim` at or before
   it (keep base ≤ snapshot or apt hits impossible downgrades). Adjust the package-name list in
   `packages.pinned.txt` only if the toolchain surface itself changes.
2. Bump `build/mainguardos/VERSION`.
3. CI `payload-reproducible` rebuilds twice and asserts an identical sha256; record the new hash.
4. Note the CVE(s) closed in the release notes.

## Versioning discipline (binding) — every production daemon/OS change bumps a version

Both update tiers deploy **on version comparison, not on content**: a rebuilt artifact at an
unchanged version is invisible to every installed machine. Two rules are therefore binding for any
production-bound change:

1. **Daemon changes bump the App/Server versions in lockstep.** Every production-bound change to
   the daemon (`Mainguard.Server`) must bump `Version`/`FileVersion`/`InformationalVersion` in
   **both** `Mainguard.Server/Mainguard.Server.csproj` and `Mainguard.App.Shell/Mainguard.App.Shell.csproj`, in the
   same commit. The tier-1 auto-updater deploys only on an app↔daemon version mismatch — a rebuilt
   daemon at an unchanged version is **never** deployed to installed VMs.
2. **OS payload-input changes cut `build/mainguardos/VERSION`.** Every change to the payload inputs —
   `build/mainguardos/Dockerfile`, `packages.pinned.txt`, `mainguardd.service`, or the daemon the
   payload embeds — must cut `build/mainguardos/VERSION`. The tier-2 upgrade offers only on a
   payload-version comparison (`/etc/mainguardos-release` vs the app-bundled stamp) — an uncut
   version means installed VMs are never offered the new payload.
3. **Sandbox jail-image source changes bump the App/Server versions in lockstep — but cut NO payload
   `VERSION`.** Each jail image (`images/mainguard-agent-base`, `images/mainguard-egress-proxy`) is
   stamped `mainguard.image.version=<sha256 of its curated build inputs>`; the expected value is a
   committed Core constant (`SandboxImageVersions`, kept honest by `SandboxImageVersionsGuardTests`).
   The **daemon** spawn preflight compares that constant against the installed image's label, so a
   changed image ⇒ a new constant ⇒ **both** the app and the daemon must carry it ⇒ a **lockstep
   App/Server bump** (rule 1's mechanism). The images ship **beside** the app (`payload/images/`,
   `docker load`ed at provisioning — the CI-built tar, else an in-VM `docker build` fallback), **not**
   inside the MainguardOS tarball, so an image-only change needs **no `build/mainguardos/VERSION` cut**.

Precedent (2026-07): the daemon-side migration-lock fix shipped as the 0.2.0→0.2.1 lockstep bump
(#201), alongside the 0.1.0→0.1.1 payload `VERSION` cut that made the new payload offerable. The
in-depth daemon-logging change followed the same discipline: 0.2.1→0.2.2 lockstep (both csprojs) +
0.1.1→0.1.2 payload `VERSION` (the embedded daemon changed). The sandbox-image version anchor
(Item 1) shipped as the **0.2.3→0.2.4 lockstep bump with NO payload `VERSION` cut** — the daemon
gained the version constant + staleness preflight but the images ship beside the app (rule 3).
MG-27/MG-36 followed the identical shape: **0.2.5→0.2.6 lockstep, no payload `VERSION` cut** — both
Dockerfiles changed (pinned base-image digests + checksummed toolchain installers) so both image
constants moved, and the daemon spawn chain changed with them (it now creates jails from the resolved
`sha256:` digest, on a per-agent network segment). A CI guard enforcing the App/Server
lockstep is a tracked follow-up (see
`docs/planning/Agent_Image_Provisioning_And_Daemon_Logging_Backlog.md`); until it lands, review is
the guard.

### Daemon logs survive upgrades (`~/.mainguard/logs`)

The per-subsystem daemon logs (`~/.mainguard/logs/<subsystem>.log`) live under `~/.mainguard`, so they
ride **tier-1** untouched (the fast-path only swaps the daemon binary) and are preserved across
**tier-2** with the rest of `.mainguard` — but the tier-2 migration **excludes the `logs/` dir** (via
`VmUpgradeCommands.ExportTreeToTar`'s exclude set, alongside `daemon.token`, and the matching
validation filter in `VmUpgradeOrchestrator`): the logs are diagnostic and can be large, so the new
distro starts with a fresh set rather than copying stale/oversized files. `/opt/mainguard` is **not** a
valid log location — tier-1 wipes it (`mv` swap) and tier-2 rebuilds it.

## How updates reach users

- The app carries the **expected payload version**. On app update / launch it compares
  `/etc/mainguardos-release`'s `MAINGUARDOS_VERSION` inside `MainguardEnv` against the expected version.
- When the installed payload is older, the app offers the **in-place VM upgrade** (P2-21 §3.6): import
  the new payload as `MainguardEnv-staging`, migrate `~/mainguard` (provisioned repos + worktrees), validate,
  then retire the old distro and promote staging — **provisioned repos are preserved** (invariant 3),
  and the old distro is never unregistered before the migration is validated.
- Kernel staleness is surfaced to the user as a `wsl --update` prompt — Mainguard never runs
  `wsl --install`/kernel installs itself.

## Non-negotiables

- **G-12:** no update or upgrade path ever calls `wsl --shutdown`; lifecycle verbs are scoped to
  `MainguardEnv` / `MainguardEnv-staging` only.
- The user's personal WSL distros are never touched by any payload update.
