#!/usr/bin/env bash
# MainguardOS payload build (P2-21 §3.5). Produces a WSL2-importable, hash-stable MainguardOS.tar.gz.
#
# Reproducibility (invariant 2): pinned base digest + pinned packages + a DETERMINISTIC repack of the
# exported rootfs (sorted entries, fixed mtime, numeric owner carried from the image, gzip -n so no
# timestamp/name lands in the gzip header). Given the same pinned inputs the sha256 is identical — CI
# double-builds and diffs. The build-inputs hash covers EVERY file `docker build` consumes — the
# Dockerfile, its COPY sources (packages.pinned.txt, mainguardd.service and the whole published
# payload/daemon/ tree), and this script — and is stamped into /etc/mainguardos-release.
#
# The extract+repack runs AS ROOT INSIDE the pinned image, and that is load-bearing twice over:
#   1. Ownership. This step used to repack with `--owner=0 --group=0`, flattening EVERY entry to
#      root:root — including /home/mainguard, which the Dockerfile creates as mainguard:mainguard via
#      `useradd -m`. mainguardd runs as uid 1000 with HOME=/home/mainguard, so its very first act
#      (SessionTokenFile.Create → Directory.CreateDirectory("~/.mainguard")) hit EACCES → unhandled
#      exception out of ConfigureServices → systemd restart loop. The image was always correct; the
#      PACKAGING broke it, which is why the daemon smoke (run against the image) never saw it.
#   2. Modes. Extracting as a non-root user makes GNU tar apply the umask and drop setuid/setgid/sticky
#      bits — /tmp shipped 0755 instead of 1777. Root extraction with -p keeps them.
# Determinism is strengthened, not weakened: tar and gzip now come from the pinned image rather than
# from whatever the build host happens to have installed.
#
# usage: build.sh [OUTPUT_DIR]          (default: build/mainguardos/out)
#        build.sh --print-inputs-hash   (print BUILD_INPUTS_HASH for the current context and exit)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

PRINT_HASH_ONLY=0
if [ "${1:-}" = "--print-inputs-hash" ]; then PRINT_HASH_ONLY=1; shift; fi

OUT_DIR="${1:-$HERE/out}"
VERSION="${MAINGUARDOS_VERSION:-$(cat "$HERE/VERSION" 2>/dev/null || echo 0.0.0-dev)}"

if [ "$PRINT_HASH_ONLY" -eq 0 ]; then
  mkdir -p "$OUT_DIR"
  echo "MainguardOS version : $VERSION"
fi

IMAGE_TAG="mainguardos-payload:${VERSION}"

# ----------------------------------------------------------------------------------------------------
# Build-inputs hash — what actually determines the payload bytes.
# ----------------------------------------------------------------------------------------------------
# This used to be `cat Dockerfile packages.pinned.txt mainguardd.service | sha256sum`, described as "the
# pinned inputs that define the payload". It omitted the two biggest contributors:
#
#   * payload/daemon/ — the published Mainguard.Server, COPY'd wholesale into /opt/mainguard. That is
#     the majority of the payload by size and ALL of its behaviour. Two payloads built from entirely
#     different daemon source carried the same BUILD_INPUTS_HASH.
#   * build.sh itself — the repack flags here (sort order, mtime, owner, gzip) decide the final bytes.
#
# The rule is now the same one SandboxImageSourceHasher.cs states for the jail images: the Dockerfile
# plus every COPY'd file. Framing is `<relative path>\0<content>`, ordinal-sorted, so content cannot
# slide between files (concatenation alone cannot tell `ab` + `c` from `a` + `bc`).
#
# It stays deterministic because the daemon publish below is deterministic — which is exactly what the
# payload-reproducible CI job's double-build already proves.
INPUT_SPECS=(
  "Dockerfile"
  "packages.pinned.txt"
  "mainguardd.service"
  "build.sh"
  "payload/daemon"        # the published daemon — COPY'd at Dockerfile:119
)

# Sets INPUTS_HASH. Must run AFTER the daemon publish, because payload/daemon/ is one of the inputs.
compute_inputs_hash() {
  # Every spec must resolve; a spec that silently vanished would quietly shrink what the hash covers.
  INPUT_FILES=()
  local spec copy_src covered rel
  for spec in "${INPUT_SPECS[@]}"; do
    if [ -d "$HERE/$spec" ]; then
      mapfile -t -O "${#INPUT_FILES[@]}" INPUT_FILES < <(cd "$HERE" && find "$spec" -type f -print)
    elif [ -f "$HERE/$spec" ]; then
      INPUT_FILES+=("$spec")
    else
      echo "::error::build-input '$spec' does not exist under $HERE — the inputs hash would silently cover less." >&2
      exit 1
    fi
  done
  mapfile -t INPUT_FILES < <(printf '%s\n' "${INPUT_FILES[@]}" | LC_ALL=C sort)

  # Coverage self-check: anything the Dockerfile COPYs must be inside the hashed set. This is the guard
  # that stops the next COPY from being added without being hashed — the exact way payload/daemon/ was
  # missed. (A directory spec covers everything beneath it.)
  while read -r copy_src; do
    copy_src="${copy_src%/}"
    covered=0
    for spec in "${INPUT_SPECS[@]}"; do
      if [ "$copy_src" = "$spec" ]; then covered=1; break; fi
      case "$copy_src" in "$spec"/*) covered=1; break;; esac
    done
    if [ "$covered" -ne 1 ]; then
      echo "::error::Dockerfile COPYs '$copy_src' but it is not covered by INPUT_SPECS — the build-inputs hash would not change when it does. Add it to INPUT_SPECS." >&2
      exit 1
    fi
  done < <(awk '
    $1 == "COPY" {
      # `COPY --from=<stage>` copies from an earlier BUILD STAGE, not from the context — that stage was
      # itself produced from inputs this loop already covers, so it is not a context input.
      for (i = 2; i <= NF; i++) if ($i ~ /^--from=/) next
      n = 0; delete src
      for (i = 2; i <= NF; i++) if ($i !~ /^--/) src[++n] = $i
      for (i = 1; i < n; i++) print src[i]   # the last argument is the destination
    }' "$HERE/Dockerfile")

  if [ "${#INPUT_FILES[@]}" -lt "${#INPUT_SPECS[@]}" ]; then
    echo "::error::only ${#INPUT_FILES[@]} build-input files resolved from ${#INPUT_SPECS[@]} specs — refusing to stamp a hash over an empty/partial input set." >&2
    exit 1
  fi

  # <relative path>\0<content> per file, ordinal-sorted (see the note above INPUT_SPECS).
  INPUTS_HASH="$(
    for rel in "${INPUT_FILES[@]}"; do
      printf '%s\0' "$rel"
      cat "$HERE/$rel"
    done | sha256sum | cut -d' ' -f1
  )"
}

# `build.sh --print-inputs-hash` prints the hash of the CURRENT context and exits — the cheap way to
# check what a change did to it. It needs payload/daemon/ to already be published (see the trap: a
# completed build removes it), which is the point: without the daemon there IS no defined payload hash.
if [ "$PRINT_HASH_ONLY" -eq 1 ]; then
  compute_inputs_hash
  printf '%s\n' "$INPUTS_HASH"
  exit 0
fi

# Publish the Mainguard daemon (mainguardd) into the docker build context BEFORE `docker build`. It is a
# self-contained linux-x64 build (the rootfs has no .NET runtime), published DETERMINISTICALLY so it
# does not undermine invariant 2: Deterministic + ContinuousIntegrationBuild normalize the compiler
# output, no ReadyToRun (its native codegen is non-reproducible), no single-file. Deterministic PORTABLE
# PDBs DO ship (DebugType=portable): the daemon logging records ex.StackTrace, and the PDBs turn those
# method-name-only frames into `…SpawnAsync() in AgentSpawnService.cs:line N` file:line diagnostics. They
# stay hash-stable because Deterministic + ContinuousIntegrationBuild normalize the compiler output and
# the PDB GUID + embedded source paths, so two back-to-back publishes are byte-identical (Mainguard.Server.pdb
# included) and the daemon layer keeps the whole tarball hash-stable — no scope carve-out needed in the
# payload-reproducible CI job. The apphost is renamed to `mainguardd`
# (it loads Mainguard.Server.dll by its embedded name, so the rename is transparent) so the running
# process comm is exactly `mainguardd` — what P2-05's `pgrep -x mainguardd` matches.
DAEMON_CTX="$HERE/payload/daemon"
echo "==> Publishing mainguardd (Mainguard.Server, linux-x64 self-contained, deterministic)…"
rm -rf "$HERE/payload"
mkdir -p "$DAEMON_CTX"
dotnet publish "$REPO_ROOT/Mainguard.Server/Mainguard.Server.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false \
  -p:DebugType=portable -p:DebugSymbols=true \
  -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
  -o "$DAEMON_CTX"
mv "$DAEMON_CTX/Mainguard.Server" "$DAEMON_CTX/mainguardd"
chmod 0755 "$DAEMON_CTX/mainguardd"

# ---- The daemon payload now exists, so the build-inputs hash is defined. ----
compute_inputs_hash
echo "Build-inputs hash : $INPUTS_HASH  (${#INPUT_FILES[@]} files)"

echo "==> Building rootfs image (pinned base + pinned packages)…"
DOCKER_BUILDKIT=1 docker build \
  --build-arg "MAINGUARDOS_VERSION=${VERSION}" \
  --build-arg "BUILD_INPUTS_HASH=${INPUTS_HASH}" \
  -t "$IMAGE_TAG" \
  "$HERE"

echo "==> Exporting + deterministically repacking rootfs…"
WORK="$(mktemp -d)"
# Tolerant cleanup: the extracted rootfs is root-owned, and on a failure path the in-container chown
# below never runs, so a plain `rm -rf` would fail and mask the real error.
trap 'rm -rf "$WORK" "$HERE/payload" 2>/dev/null || true' EXIT

CID="$(docker create "$IMAGE_TAG")"
docker export "$CID" -o "$WORK/rootfs.tar"
docker rm "$CID" >/dev/null

# Extract + repack as root inside the pinned image (see the header): -p and --numeric-owner on the way
# in preserve the image's real uid/gid and high mode bits; sorted names + zero mtime + numeric owner +
# gzip -n on the way out keep the tarball byte-stable. The final chown hands the artifact back to the
# invoking user so the host-side mv and the cleanup trap work unprivileged.
TARBALL="$OUT_DIR/MainguardOS.tar.gz"
docker run --rm \
  -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  -v "$WORK:/work" \
  "$IMAGE_TAG" bash -c '
    set -euo pipefail
    mkdir -p /work/rootfs
    tar -p --numeric-owner -xf /work/rootfs.tar -C /work/rootfs
    tar --sort=name \
        --mtime="@0" \
        --numeric-owner \
        --format=gnu \
        -C /work/rootfs -cf - . \
      | gzip -n -9 > /work/MainguardOS.tar.gz
    rm -rf /work/rootfs /work/rootfs.tar
    chown "$HOST_UID:$HOST_GID" /work/MainguardOS.tar.gz
  '
mv "$WORK/MainguardOS.tar.gz" "$TARBALL"

SHA="$(sha256sum "$TARBALL" | cut -d' ' -f1)"
echo "$SHA  MainguardOS.tar.gz" > "$OUT_DIR/MainguardOS.tar.gz.sha256"
printf 'MAINGUARDOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nTARBALL_SHA256=%s\n' \
  "$VERSION" "$INPUTS_HASH" "$SHA" > "$OUT_DIR/mainguardos-release"

echo "==> Done."
echo "    tarball : $TARBALL"
echo "    sha256  : $SHA"
