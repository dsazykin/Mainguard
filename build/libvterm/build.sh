#!/usr/bin/env bash
# =============================================================================
# P2-18: build the pinned libvterm as a shared library — DAEMON-SIDE ONLY.
# =============================================================================
# The server-side terminal engine (Mainguard.Agents/Terminal/Vterm/) P/Invokes libvterm.
# CI runs this script and hands the output to the test run (MAINGUARD_LIBVTERM) and to the
# daemon publish (Mainguard.Server bundles build/libvterm/out/libvterm.{so,dylib} when
# present — the client never ships or loads it; that is a P2-18 invariant).
#
# Platform-aware: Linux emits libvterm.so (ELF soname), macOS emits libvterm.dylib
# (-install_name) for the macos-host substrate's native daemon. Same pinned source, same
# flags, one script.
#
# The source is PINNED: exact upstream release tarball + sha256. Changing the pin is a
# reviewed change to this file, never an ambient "latest".
#
# No libtool/perl needed: the release tarball ships the generated encoding tables, so a
# direct C compile suffices (same sources, same -std=c99 the upstream Makefile uses).
#
# Usage:  build/libvterm/build.sh [out-dir]     # default: build/libvterm/out
# Needs:  cc (gcc/clang), curl, tar, sha256sum (Linux) / shasum (macOS).
set -euo pipefail

VERSION="0.3.3"
URL="https://www.leonerd.org.uk/code/libvterm/libvterm-${VERSION}.tar.gz"
SHA256="09156f43dd2128bd347cbeebe50d9a571d32c64e0cf18d211197946aff7226e0"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${1:-$HERE/out}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# sha256sum is GNU coreutils; macOS ships shasum. One shim, used for verify AND stamp.
sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

case "$(uname -s)" in
  Darwin) LIBNAME="libvterm.dylib"
          LINKFLAGS=(-dynamiclib -install_name "@rpath/libvterm.0.dylib") ;;
  *)      LIBNAME="libvterm.so"
          LINKFLAGS=(-shared -Wl,-soname,libvterm.so.0) ;;
esac

echo "libvterm $VERSION → $OUT/$LIBNAME"

TARBALL="$WORK/libvterm-$VERSION.tar.gz"
curl -fsSL --retry 3 -o "$TARBALL" "$URL"
echo "$SHA256  $TARBALL" | sha256 -c - >/dev/null

tar -xzf "$TARBALL" -C "$WORK"
SRC="$WORK/libvterm-$VERSION"

CC="${CC:-cc}"
mkdir -p "$OUT"
"$CC" -O2 -fPIC -std=c99 -Wall \
  "${LINKFLAGS[@]}" \
  -I"$SRC/include" \
  "$SRC"/src/*.c \
  -o "$OUT/$LIBNAME"

sha256 "$OUT/$LIBNAME" | tee "$OUT/$LIBNAME.sha256"
echo "OK: $(file "$OUT/$LIBNAME" 2>/dev/null || echo built)"
