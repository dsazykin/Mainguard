#!/usr/bin/env bash
# =============================================================================
# The automated Client-closure gate (ADR-0001 payoff) — step 2h.
# =============================================================================
# Publishes an exe HEAD and asserts its dependency closure is FREE of the agent
# platform. This is the AUTOMATED version of the manual `.deps.json` proof: the
# whole point of the two-head split (Mainguard.Client.App / Mainguard.Pro.App)
# is that the plain Git-client head references Mainguard.App.Shell ONLY, so its
# published closure physically cannot contain the agent platform. This script
# proves it every CI run — and you can run it LOCALLY the same way CI does.
#
# It inspects the published `<head>.deps.json` (the authoritative runtime closure
# manifest) AND the publish output dir for any of these assemblies:
#     Mainguard.Agents, Mainguard.Agents.UI, Mainguard.Protos,
#     Docker.DotNet, Porta.Pty, Grpc
#
# ---------------------------------------------------------------------------
# GATE THE CLOSURE THAT SHIPS
# ---------------------------------------------------------------------------
# This used to publish FRAMEWORK-DEPENDENT (no -r, no --self-contained) while
# what actually ships — pack.ps1, package-smoke.yml — is self-contained win-x64.
# A RID-specific publish resolves a DIFFERENT asset graph (RID-specific package
# assets, native bits, runtime packs), so the closure being gated was not the
# closure being distributed. It now defaults to the shipping shape; --rid none
# restores the framework-dependent publish for a quick local check.
#
# A RID restore adds a `net10.0/<rid>` target that a plain restore strips again,
# so this would otherwise churn every committed packages.lock.json (MG-35, same
# reasoning as ci.yml's payload-reproducible job). The lockfiles are snapshotted
# and put back afterwards; nothing is lost, because the RID graph is the RID-less
# graph plus SDK-supplied runtime packs and both halves are pinned already.
#
# ---------------------------------------------------------------------------
# THE POSITIVE CONTROL ASSERTS EVERY TOKEN
# ---------------------------------------------------------------------------
# It used to pass if ANY ONE of the six matched. Typo `Porta.Pty` -> `Porta.Pyt`
# and that token silently stopped being detected forever, while the control kept
# reporting PASS because `Mainguard.Agents` still matched. A control that cannot
# distinguish "five matchers work" from "six matchers work" is not a control.
# Present mode now requires ALL of them.
#
# Usage:
#   build/ci/verify-client-closure.sh                      # gate the Client head (must be FREE)
#   build/ci/verify-client-closure.sh --head Mainguard.Pro.App --mode present
#                                                          # positive control (must CONTAIN, every token)
#   build/ci/verify-client-closure.sh --publish-dir <dir>  # inspect an existing publish dir
#   build/ci/verify-client-closure.sh --rid none           # framework-dependent publish (fast local check)
#
# Portable: uses `dotnet` if present (CI / Linux), else `dotnet.exe` (local WSL).
# Override with DOTNET_CLI=<path>. Publishes to a gitignored `.../publish/` dir.
#
# Exit: 0 = assertion held, 1 = assertion FAILED (the gate), 2 = usage/tooling error.
set -euo pipefail

# The agent-platform assemblies the Client head's closure must never contain.
FORBIDDEN=(
  "Mainguard.Agents"
  "Mainguard.Agents.UI"
  "Mainguard.Protos"
  "Docker.DotNet"
  "Porta.Pty"
  "Grpc"
)

HEAD="Mainguard.Client.App"
MODE="absent"        # absent = the real gate (must be FREE); present = positive control (must CONTAIN)
PUBLISH_DIR=""       # supply to skip publishing and inspect an existing dir
RID="win-x64"        # what actually ships; `none` = framework-dependent

usage() { sed -n '2,60p' "${BASH_SOURCE[0]}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --head)        HEAD="${2:?--head needs a project name}"; shift 2 ;;
    --mode)        MODE="${2:?--mode needs absent|present}"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="${2:?--publish-dir needs a path}"; shift 2 ;;
    --rid)         RID="${2:?--rid needs a RID or 'none'}"; shift 2 ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$MODE" in absent|present) ;; *) echo "::error::--mode must be 'absent' or 'present'" >&2; exit 2 ;; esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

# Pick the .NET CLI. CI runners have `dotnet`; a local WSL shell usually only has `dotnet.exe`.
DOTNET="${DOTNET_CLI:-}"
if [ -z "$DOTNET" ]; then
  if command -v dotnet    >/dev/null 2>&1; then DOTNET="dotnet"
  elif command -v dotnet.exe >/dev/null 2>&1; then DOTNET="dotnet.exe"
  else echo "::error::neither 'dotnet' nor 'dotnet.exe' is on PATH (set DOTNET_CLI)" >&2; exit 2; fi
fi

if [ -z "$PUBLISH_DIR" ]; then
  PROJ="${HEAD}/${HEAD}.csproj"
  [ -f "$PROJ" ] || { echo "::error::project not found: $PROJ (run from the repo, pass --head)" >&2; exit 2; }
  # The 'publish' leaf is matched by .gitignore's [Pp]ublish/, so nothing here is ever tracked.
  PUBLISH_DIR="artifacts/closure-check/${HEAD}/publish"

  PUBLISH_ARGS=(publish "$PROJ" -c Release -o "$PUBLISH_DIR" -v minimal --nologo)
  if [ "$RID" = "none" ]; then
    echo "==> Publishing ${HEAD} (framework-dependent, Release) via ${DOTNET} …"
  else
    PUBLISH_ARGS+=(-r "$RID" --self-contained true)
    echo "==> Publishing ${HEAD} (self-contained ${RID}, Release — the shape that ships) via ${DOTNET} …"
  fi

  # Snapshot the committed lockfiles: a RID restore rewrites them (see the header) and a gate must not
  # leave the working tree dirty.
  LOCK_SNAPSHOT="artifacts/closure-check/.lockfiles"
  rm -rf "$LOCK_SNAPSHOT"
  mapfile -t LOCKFILES < <(find . -name packages.lock.json -not -path './artifacts/*' | LC_ALL=C sort)
  for lf in "${LOCKFILES[@]}"; do
    mkdir -p "$LOCK_SNAPSHOT/$(dirname "$lf")"
    cp "$lf" "$LOCK_SNAPSHOT/$lf"
  done
  restore_lockfiles() {
    for lf in "${LOCKFILES[@]}"; do
      if [ -f "$LOCK_SNAPSHOT/$lf" ] && ! cmp -s "$LOCK_SNAPSHOT/$lf" "$lf"; then
        cp "$LOCK_SNAPSHOT/$lf" "$lf"
        echo "    (restored $lf — the RID restore had rewritten it)"
      fi
    done
    rm -rf "$LOCK_SNAPSHOT"
  }
  trap restore_lockfiles EXIT

  rm -rf "$PUBLISH_DIR"
  LOG="$(mktemp)"
  if ! "$DOTNET" "${PUBLISH_ARGS[@]}" >"$LOG" 2>&1; then
    echo "::error::dotnet publish failed for ${PROJ}:" >&2; cat "$LOG" >&2; rm -f "$LOG"; exit 2
  fi
  rm -f "$LOG"
fi

# The head's OWN runtime closure manifest. Pick it by name: a Pro publish co-locates the elevated
# helper's .deps.json too, so "whichever sorts first" would inspect the wrong one.
DEPS="$PUBLISH_DIR/$HEAD.deps.json"
if [ ! -f "$DEPS" ]; then
  DEPS="$(ls "$PUBLISH_DIR"/*.deps.json 2>/dev/null | head -n1 || true)"
fi
[ -n "$DEPS" ] && [ -f "$DEPS" ] || { echo "::error::no .deps.json under ${PUBLISH_DIR}" >&2; exit 2; }

echo "==> Head        : ${HEAD}"
echo "==> Publish dir : ${PUBLISH_DIR}"
echo "==> Closure     : ${DEPS}"
echo "==> Mode        : ${MODE} (absent = must be FREE of the agent platform; present = positive control)"
echo "----------------------------------------------------------------------"

HITS=()
MISSES=()
for tok in "${FORBIDDEN[@]}"; do
  found=""
  # 1) the authoritative closure manifest (library keys + runtime file entries)
  grep -Fq "$tok" "$DEPS" && found="deps.json"
  # 2) belt & suspenders: an actual assembly of that name anywhere in the publish output. The scan is
  #    RECURSIVE — a non-recursive glob missed anything under a subdirectory, and a self-contained
  #    publish has several (elevated-stage/, payload/, runtimes/…).
  if [ -n "$(find "$PUBLISH_DIR" -type f -name "${tok}*.dll" -print -quit)" ]; then
    found="${found:+$found + }publish dir"
  fi
  if [ -n "$found" ]; then
    HITS+=("$tok")
    printf '  [FOUND]   %-20s via %s\n' "$tok" "$found"
  else
    MISSES+=("$tok")
    printf '  [absent]  %s\n' "$tok"
  fi
done

echo "----------------------------------------------------------------------"

if [ "$MODE" = "present" ]; then
  # Positive control: prove the gate is not vacuous — the Pro head SHOULD carry the platform, and it
  # should carry EVERY token. Passing on "at least one hit" meant a mistyped or retired token could
  # stop matching anything, forever, while this still printed PASS on the strength of its neighbours.
  if [ "${#HITS[@]}" -eq "${#FORBIDDEN[@]}" ]; then
    echo "POSITIVE CONTROL PASS: ${HEAD} carries all ${#FORBIDDEN[@]} agent-platform tokens — every matcher demonstrably works."
    exit 0
  fi
  echo "::error::POSITIVE CONTROL FAILED: ${HEAD} matched only ${#HITS[@]} of ${#FORBIDDEN[@]} agent-platform tokens." >&2
  echo "::error::Unmatched: ${MISSES[*]} — either the token is misspelled/retired (fix FORBIDDEN) or the Pro head genuinely stopped shipping it (then the gate below is no longer testing what it claims)." >&2
  exit 1
fi

# Default 'absent' mode: the real gate.
if [ "${#HITS[@]}" -gt 0 ]; then
  echo "::error::CLOSURE GATE FAILED: ${HEAD} pulled the agent platform into its closure — the Client head must be free of it: ${HITS[*]}" >&2
  exit 1
fi
echo "CLOSURE GATE PASS: ${HEAD} closure is FREE of the agent platform (all ${#FORBIDDEN[@]} tokens absent)."
exit 0
