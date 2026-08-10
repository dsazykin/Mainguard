# MainguardOS payload build (P2-21)

Reproducible build of `MainguardOS.tar.gz` — the WSL2 root filesystem Mainguard imports as the
`MainguardEnv` distro (P2-05). This is the "payload pipeline" half of P2-21.

## Files

| File | Role |
|---|---|
| `Dockerfile` | Rootfs recipe (Debian **bookworm**). Base image pinned **by digest**; apt pinned at a frozen **`snapshot.debian.org`** timestamp (`DEBIAN_SNAPSHOT`); installs the package set; `COPY`s the published daemon to `/opt/mainguard/`; enables the `mainguardd` systemd unit; writes `/etc/wsl.conf` (boot systemd + default `mainguard` user — deliberately **no** `[boot] command` for dockerd: systemd already starts it, and a boot command double-started it into the stale-pidfile loop) and the versioned `/etc/mainguardos-release` stamp. |
| `packages.pinned.txt` | The curated package **name** list (versions come from the snapshot pin, not per-line). Bump the version floor by moving `DEBIAN_SNAPSHOT`; see `docs/mainguardos-updates.md`. Includes `systemd`/`systemd-sysv` so the daemon can be a supervised unit. |
| `mainguardd.service` | The systemd unit that supervises the daemon (`/opt/mainguard/mainguardd`, loopback gRPC, `User=mainguard`, `Restart=on-failure`). Shipped **enabled** (started on first boot). |
| `VERSION` | The MainguardOS payload version (stamped into the release file). |
| `build.sh` | Publishes the daemon (`dotnet publish Mainguard.Server -r linux-x64`, deterministic) into `payload/daemon/`, builds the image, exports the rootfs, and **deterministically repacks** it into `out/MainguardOS.tar.gz` (+ `.sha256`, + `mainguardos-release`). |
| `.dockerignore` | Keeps prior `out/` tarballs out of the docker build context (keeps `payload/daemon/`). |

## The Mainguard daemon (`mainguardd`) in the payload

The imported VM must have Docker **and** the Mainguard orchestration daemon running, or the Windows app
connects to nothing and no agent can spawn/verify. So the payload carries the daemon:

- **What.** `mainguardd` is the published `Mainguard.Server` (linux-x64, **self-contained** — the rootfs has
  no .NET runtime). `build.sh` publishes it into the docker context and the Dockerfile `COPY`s it to
  `/opt/mainguard/`. The published apphost is renamed `Mainguard.Server` → `mainguardd` (the apphost loads
  `Mainguard.Server.dll` by its embedded name, so the rename is transparent), so the running process's
  `comm` is exactly `mainguardd` — what P2-05's `pgrep -x mainguardd` / `pgrep -f mainguardd` health checks
  match.
- **How it starts.** `/etc/wsl.conf` sets `[boot] systemd=true`, and `mainguardd.service` is shipped
  **enabled** (its `multi-user.target.wants` symlink is written at build time). So on first boot systemd
  brings the daemon up automatically, alongside dockerd (started by systemd via the docker.io package's
  enabled units — there is deliberately **no** `[boot] command`, which used to double-start dockerd into
  the stale-pidfile "start request repeated too quickly" loop). P2-05 `StartDaemonStep`'s
  `systemctl start mainguardd` is then only a repair path — `pgrep` already matches on a healthy boot.
- **Reachability.** The daemon binds **loopback `127.0.0.1:5250` only** (invariant 2). WSL2
  `localhostForwarding` relays the Windows app's `127.0.0.1:5250` connection into the in-VM listener, so
  no non-loopback bind is ever needed. **That relay is also why loopback is not a trust boundary (MG-19):**
  the port is reachable from any process in the Windows user's session and from the user's other distros,
  so the listener serves **mutually-authenticated, pinned TLS** rather than h2c — see
  `docs/security-architecture.md`.

## Reproducibility of the daemon layer (interaction with invariant 2)

A freshly-published .NET binary is not byte-identical build-to-build **by default** (fresh MVID,
embedded timestamps). Since the daemon is `COPY`'d into the image, it is part of the exported rootfs and
therefore part of the compared tarball hash — so it must be **deterministic** or it would break the
`payload-reproducible` CI job. `build.sh` publishes it deterministically and this keeps the WHOLE tarball
hash-stable (no scope carve-out in the CI job):

- `-p:Deterministic=true -p:ContinuousIntegrationBuild=true` — normalized MVID + embedded paths, zeroed
  PE timestamps.
- `-p:PublishReadyToRun=false` — R2R native codegen is **not** reproducible.
- `-p:PublishSingleFile=false`, `-p:PublishTrimmed=false` — no single-file bundle, no trimming (each a
  determinism/size variable removed).
- `-p:DebugType=portable -p:DebugSymbols=true` — ship a deterministic **portable PDB**
  (`Mainguard.Server.pdb`) alongside the DLL so the daemon logging's `ex.StackTrace` carries
  `…() in <file>.cs:line N` instead of method names only. Deterministic + ContinuousIntegrationBuild
  normalize the PDB GUID and embedded source paths, so the PDB is byte-identical build-to-build and does
  not break the hash-stable invariant.

Two back-to-back publishes on the same runner are byte-identical (verified: `diff -rq` clean across
every published file, `Mainguard.Server.pdb` included), so the tarball's sha256 is stable across the CI
double-build. The `build-inputs
hash` in `/etc/mainguardos-release` now also covers `mainguardd.service` (a pinned input); the daemon
**binary** is a versioned build artifact tracked by `MAINGUARDOS_VERSION` / the app version, not an apt
pin, so it is deliberately not folded into that hash — its reproducibility is guaranteed by the
deterministic publish above, and the tarball sha256 covers it end-to-end.

## Build

```bash
build/mainguardos/build.sh            # → build/mainguardos/out/MainguardOS.tar.gz
MAINGUARDOS_VERSION=0.1.0 build/mainguardos/build.sh
```

Requires Docker **and** the .NET SDK (pinned by `global.json` — `build.sh` publishes the daemon before
the docker build). The App's OOBE (P2-21) and the P2-05 bootstrapper import the resulting tarball via
`wsl --import MainguardEnv <installDir> MainguardOS.tar.gz --version 2`.

## Reproducibility (invariant 2 — hash-stable given pinned inputs)

Determinism comes from two pins plus a deterministic repack:

1. **Base image pinned by digest** (not a tag) in the `FROM` line — a *dated* `debian:bookworm-…-slim`
   image that predates the snapshot below (so installs only ever upgrade, never hit an impossible
   downgrade).
2. **Apt pinned at a frozen `snapshot.debian.org` timestamp** (`DEBIAN_SNAPSHOT`, e.g.
   `20250601T000000Z`) for `bookworm main` + `bookworm-security`. The snapshot freezes the whole
   archive at one instant, so installing a package **name** resolves to one deterministic version —
   and, unlike an exact per-version pin, that version stays fetchable forever (mirrors never retire a
   snapshot). The snapshot is signed by the normal Debian archive keys already in
   `debian-archive-keyring`, so the fetch stays authenticated (no `AllowUnauthenticated`); its Release
   files are old, so `Acquire::Check-Valid-Until` is turned off.
3. **Deterministic tar**: `--sort=name --mtime=@0 --numeric-owner --format=gnu`, then `gzip -n`
   (no timestamp/name in the gzip header). **Ownership is PRESERVED from the image, never flattened**
   — the old `--owner=0 --group=0` flags stole `/home/mainguard` from the service user and crash-looped
   the daemon; do not reintroduce them. Note the GNU tar format carries **no xattrs/file
   capabilities** (`security.capability` is dropped): nothing in the current package set needs them,
   but a future package that ships a setcap binary would lose its capabilities silently — check
   before adding one.

The `build-inputs hash` covers **every file `docker build` consumes** — the `Dockerfile`, each of its
`COPY` sources (`packages.pinned.txt`, `mainguardd.service`, and the whole published `payload/daemon/`
tree) and `build.sh` itself — framed as `<relative path>\0<content>` per file, ordinal-sorted, then
sha256'd. It is stamped into `/etc/mainguardos-release` (alongside `DEBIAN_SNAPSHOT` and the base
digest), so the payload self-describes exactly what produced it. `build.sh --print-inputs-hash` prints
it for the current context.

> It used to be `sha256(Dockerfile + packages.pinned.txt + mainguardd.service)`, which read none of the
> daemon — the majority of the payload and all of its behaviour — so two payloads built from entirely
> different `Mainguard.Server` source carried the same hash, and changes to `build.sh`'s repack flags
> were invisible too. `build.sh` now refuses to stamp a hash if the Dockerfile `COPY`s anything that is
> not in `INPUT_SPECS`, which is how that omission is kept from recurring.
>
> The version is stamped separately (`MAINGUARDOS_VERSION`), so it is deliberately **not** an input to
> this hash: the pair (version, inputs hash) describes the payload, and a version bump alone should not
> look like an input change.

CI (`payload-reproducible` job) builds the tarball **twice** and asserts an identical sha256, that both
runs stamped the same 64-hex `BUILD_INPUTS_HASH` (never the `unknown` `ARG` default, never empty), and
that the value inside the shipped rootfs matches the one the build computed.

### Bumping the version floor (CVE cadence)

To take newer packages (security fixes), move `DEBIAN_SNAPSHOT` to a **later** real snapshot timestamp
(`YYYYMMDDTHHMMSSZ` — any instant where bookworm exists; snapshot.debian.org redirects to the nearest
snapshot at or before it, deterministically). If the new snapshot predates the pinned base image you
will hit downgrade conflicts, so bump the base `FROM` digest to a dated `debian:bookworm-…-slim` at or
before the snapshot in the **same commit**. Both are deliberate, digest/timestamp-pinned inputs — never
floating — so the reproducibility invariant (pinned in → stable out) still holds.

> Note: snapshot.debian.org is rate-limited and slow (the package-list fetch can take a couple of
> minutes); the Dockerfile sets generous `Acquire::Retries`/`Timeout` to absorb that.
