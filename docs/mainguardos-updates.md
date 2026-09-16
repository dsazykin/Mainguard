# MainguardOS payload — updates & CVE cadence

How `MainguardOS.tar.gz` (the WSL2 `MainguardEnv` root filesystem, built under `build/mainguardos/`)
is patched and how updates reach installed users.

**If you change anything shipped to users, read [Versioning discipline](#versioning-discipline-binding)
first.** Both update tiers deploy on *version comparison, not content* — a rebuilt artifact at an
unchanged version is invisible to every installed machine, silently.

## Scope

| Layer | Patched here? | Notes |
|---|---|---|
| Base image | Yes | A *dated* `debian:bookworm-…-slim` pinned by digest, bumped on cadence, kept at or before `DEBIAN_SNAPSHOT`. |
| `DEBIAN_SNAPSHOT` | Yes | The frozen `snapshot.debian.org` timestamp apt resolves against. Moving it is how the package version floor advances. |
| `packages.pinned.txt` | Yes | A curated package **name** list; versions come from the snapshot pin, not per-line. |
| `mainguardd` | Yes — rebuilt each payload build | Published deterministically (`Deterministic` + `ContinuousIntegrationBuild`, no ReadyToRun/single-file) so the tarball stays byte-reproducible. Portable PDBs ride along, so daemon `ex.StackTrace` carries file:line. |
| WSL2 kernel | **No — deferred to WSL** | Microsoft's, updated by `wsl --update`. Diagnostics surface `WslInstallState.NeedsKernelUpdate`; we never bundle a kernel. |
| Agent-base container image | Separate pipeline | `images/mainguard-agent-base` (P2-07) has its own build + CVE flow. |

## Cadence

- **Monthly.** First week of the month: move `DEBIAN_SNAPSHOT` forward, bump the base digest to a
  dated slim image at or before it, rebuild, cut a new payload `VERSION`.
- **Out-of-band.** A `Critical` or actively-exploited CVE in an in-scope package (docker, git, openssh,
  ca-certificates, base libc) bumps the snapshot within the security SLA — don't wait for the train.
- **Deliberate pins only.** Floating the base tag or dropping the snapshot pin is a review rejection.

> **Currently lapsed.** `DEBIAN_SNAPSHOT` is pinned at `20250601T000000Z` and has not moved since
> 2026-07-14. Tracked in [`STATUS.md`](STATUS.md#open-items-outside-the-task-list).

### Making a bump

1. In `build/mainguardos/Dockerfile`: move `DEBIAN_SNAPSHOT` to a real snapshot timestamp
   (`YYYYMMDDTHHMMSSZ`) and bump the `FROM` digest to a dated slim image **at or before** it — base
   after snapshot makes apt attempt impossible downgrades. Touch `packages.pinned.txt` only if the
   toolchain surface itself changes.
2. Bump `build/mainguardos/VERSION`.
3. CI `payload-reproducible` rebuilds twice and asserts an identical sha256. Record the new hash.
4. Note the CVEs closed in the release notes.

All in the **same commit**, so the `build-inputs hash` in `/etc/mainguardos-release` changes
intentionally and reproducibility holds: pinned in → stable out.

## Versioning discipline (binding)

| Change | App/Server lockstep bump? | Cut payload `VERSION`? |
|---|---|---|
| Daemon (`Mainguard.Server`) | **Yes** — both `Mainguard.Server.csproj` and `Mainguard.App.Shell.csproj`, same commit | Only if the payload embeds the change |
| Payload inputs (`Dockerfile`, `packages.pinned.txt`, `mainguardd.service`, embedded daemon) | — | **Yes** |
| Jail image source (`images/mainguard-agent-base`, `mainguard-egress-proxy`) | **Yes** | **No** |

Why the third row: each jail image is stamped `mainguard.image.version=<sha256 of its build inputs>`,
and the expected value is a committed constant (`SandboxImageVersions`, held honest by
`SandboxImageVersionsGuardTests`). The daemon's spawn preflight compares constant against label, so a
changed image ⇒ new constant ⇒ both app and daemon must carry it. The images ship **beside** the app
(`payload/images/`, `docker load`ed at provisioning), not inside the tarball — hence no `VERSION` cut.

Tier-1 deploys only on an app↔daemon version mismatch. Tier-2 offers only on a payload-version
comparison (`/etc/mainguardos-release` vs the app-bundled stamp). Miss the bump and the fix never
ships.

> **No CI guard enforces this.** Review is the only check. Tracked in
> [`STATUS.md`](STATUS.md#open-items-outside-the-task-list).

<details>
<summary>Precedents</summary>

- **0.2.0→0.2.1** lockstep (#201) + **0.1.0→0.1.1** payload — daemon migration-lock fix.
- **0.2.1→0.2.2** lockstep + **0.1.1→0.1.2** payload — daemon logging (the payload embeds the daemon).
- **0.2.3→0.2.4** lockstep, **no** payload cut — sandbox image version anchor (rule 3).
- **0.2.5→0.2.6** lockstep, **no** payload cut — MG-27/MG-36: both Dockerfiles changed, so both image
  constants moved, and the daemon now creates jails from the resolved `sha256:` digest.

</details>

## Reaching users

- The app carries the expected payload version and compares it against `MAINGUARDOS_VERSION` in
  `/etc/mainguardos-release` inside `MainguardEnv`.
- If older, it offers the **in-place VM upgrade**: import the new payload as `MainguardEnv-staging`,
  migrate `~/mainguard`, validate, then retire the old distro and promote staging. Provisioned repos
  are preserved, and the old distro is never unregistered before the migration validates.
- Kernel staleness becomes a `wsl --update` prompt. Mainguard never runs `wsl --install` itself.

**Daemon logs.** `~/.mainguard/logs/<subsystem>.log` rides tier-1 untouched (it only swaps the
binary). Tier-2 preserves `.mainguard` but **excludes `logs/`** — diagnostic and potentially large, so
the new distro starts fresh. `/opt/mainguard` is never a valid log location: tier-1 wipes it (`mv`
swap) and tier-2 rebuilds it.

## Non-negotiables

- **G-12:** no update or upgrade path ever calls `wsl --shutdown`. Lifecycle verbs are scoped to
  `MainguardEnv` / `MainguardEnv-staging` only.
- The user's personal WSL distros are never touched.
