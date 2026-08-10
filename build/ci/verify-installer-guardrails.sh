#!/usr/bin/env bash
# =============================================================================
# P2-21 §7 rejection-trigger guard — RunOnce / --shutdown must not exist.
# =============================================================================
# Two prohibitions, both from the P2-21 reviewer script:
#
#   * RunOnce     — resume MUST use the elevated, self-deleting Scheduled Task. A HKCU RunOnce value
#                   runs UNELEVATED after the reboot, so the resume would silently do nothing.
#   * --shutdown  — G-12 forbids the VM-WIDE wsl shutdown verb; lifecycle is scoped to MainguardEnv
#                   (`--terminate` / `--unregister`).
#
# WHY THIS IS A SCRIPT AND NOT FOUR LINES OF YAML
# -----------------------------------------------
# The previous inline version scanned `installer/ build/mainguardos/` only. Both of those trees are
# thin drivers; the command construction that could actually violate either rule lives in
# Mainguard.Agents (InstallerCommands.RegisterResumeTask, WslCommands) and Mainguard.Server. The
# guard was therefore grepping the two places the violation could not be.
#
# It was also VACUOUS-SAFE in the wrong direction: `grep -r … dir/ 2>/dev/null` exits 2 when the path
# is missing, the redirect ate the diagnostic, `if` saw non-zero and the job printed "clean". Renaming
# either directory would have made this guard report success having read zero bytes, forever. So:
#   * every root must EXIST (hard failure otherwise), and
#   * every root must yield at least one scannable file (a vacuous run is a failure, not a pass),
#   * the file list is built explicitly and its size is printed, so "scanned nothing" is visible.
#
# WHAT A TEXT SEARCH CANNOT DO
# ----------------------------
# It cannot see a verb composed at runtime ("--" + "shutdown"). That is covered separately by
# WslCommands.AllBuilders() + the G-12 unit test in Mainguard.Tests/BootstrapStateMachineTests.cs,
# which asserts over the REAL emitted argument lists. This script is the cheap outer net, not the
# only one.
#
# PROSE IS NOT A VIOLATION
# ------------------------
# InstallerCommands.cs documents "Never RunOnce" in its XML doc comment — the prohibition being
# written down is the opposite of the prohibition being broken. Matches on lines that are pure
# comments (`//`, `///`, `*`, `#`, `<!--`) are therefore ignored; a match anywhere in CODE fails.
#
# Usage:  build/ci/verify-installer-guardrails.sh
# Exit:   0 = clean, 1 = violation found, 2 = the guard could not scan (missing root / empty tree).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

# The trees that can contain either construction. Mainguard.Agents and Mainguard.Server are the two the
# original guard dropped, and they are the two that matter — installer/ only drives them.
ROOTS=(
  "Mainguard.Agents"
  "Mainguard.Server"
  "installer"
  "build/mainguardos"
)

# name|grep-pattern|failure message
CHECKS=(
  "RunOnce|RunOnce|resume MUST use the elevated Scheduled Task, never a RunOnce registry value (P2-21 §7)"
  "--shutdown|--shutdown|G-12 forbids the VM-wide wsl shutdown verb; scope lifecycle to MainguardEnv"
)

# Generated / derived output. The same basenames are pruned by `find` (the count) and by `grep
# --exclude-dir` (the scan), so the number printed below is the number of files actually searched.
PRUNE=(obj bin out payload node_modules)
GREP_EXCLUDES=()
for d in "${PRUNE[@]}"; do GREP_EXCLUDES+=("--exclude-dir=$d"); done

TOTAL=0
for root in "${ROOTS[@]}"; do
  # Precondition, NOT a skip: a root that is missing means the guard is being run against a tree it no
  # longer understands (a rename/move), and "cannot scan" must never be reported as "clean".
  if [ ! -d "$root" ]; then
    echo "::error::installer guardrail root '$root' does not exist — the guard cannot scan it. Update ROOTS in $0 if the tree moved." >&2
    exit 2
  fi

  # Count what will be scanned, and assert it. The count is an assertion in its own right, not a
  # side effect of grep's exit code.
  count="$(find "$root" \
      -type d \( -name obj -o -name bin -o -name out -o -name payload -o -name node_modules \) -prune -o \
      -type f -print | wc -l)"
  if [ "$count" -eq 0 ]; then
    echo "::error::installer guardrail root '$root' yielded 0 scannable files — a vacuous scan is a failure, not a pass." >&2
    exit 2
  fi
  printf '  %-22s %4d files\n' "$root" "$count"
  TOTAL=$((TOTAL + count))
done

echo "==> Scanning $TOTAL files across ${#ROOTS[@]} roots for the P2-21 §7 rejection triggers."
if [ "$TOTAL" -eq 0 ]; then
  echo "::error::0 files scanned — the guardrail check is vacuous." >&2
  exit 2
fi

FAILED=0
for check in "${CHECKS[@]}"; do
  IFS='|' read -r name pattern message <<<"$check"

  # grep exits 1 for "no match" and >1 for a real error; only the former may be swallowed. stderr is
  # NOT redirected to /dev/null — swallowing grep's diagnostic is what hid the original hole.
  set +e
  raw="$(grep -rnIi -F "${GREP_EXCLUDES[@]}" -e "$pattern" -- "${ROOTS[@]}")"
  rc=$?
  set -e
  if [ "$rc" -gt 1 ]; then
    echo "::error::grep failed (exit $rc) while scanning for '$name' — the guard did not complete." >&2
    exit 2
  fi

  # Drop pure-comment hits: the prohibition being DOCUMENTED is not the prohibition being broken.
  hits="$(printf '%s\n' "$raw" | awk '
    NF == 0 { next }
    {
      body = $0
      sub(/^[^:]*:[0-9]+:/, "", body)
      sub(/^[[:space:]]+/, "", body)
      if (body ~ /^(\/\/|\*|#|<!--)/) next
      print
    }')"

  if [ -n "$hits" ]; then
    echo "::error::'$name' found — $message"
    printf '%s\n' "$hits" >&2
    FAILED=1
  else
    echo "  [clean] $name"
  fi
done

if [ "$FAILED" -ne 0 ]; then
  exit 1
fi

echo "Installer guardrails clean (no RunOnce, no --shutdown) across $TOTAL scanned files."
