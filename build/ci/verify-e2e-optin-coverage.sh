#!/usr/bin/env bash
# =============================================================================
# Ticket #65 — every opt-in E2E test must be wired into a job that RUNS it.
# =============================================================================
# `[RequiresDockerAndOptInFact]` marks a test that skips unless MAINGUARD_VERIFY_E2E=1. That gate is
# deliberate (the two current ones are a 90-minute in-jail solution build and a 30-minute Python
# toolchain install), but for the whole life of the attribute NO workflow set the variable. The
# result was not a slow test suite; it was two tests that could never fail, reported as "2 skipped"
# in a green run, covering the exact path that decides whether an agent's work may merge.
#
# The failure mode is INHERITANCE: nobody chose to exclude them from CI: the exclusion was simply
# never revisited. So the fix is not only "add a workflow" — a workflow can be deleted, renamed, or
# have a test renamed out from under it, and the tests go quiet again with nothing failing. This
# guard runs on EVERY pull request and makes the wiring an invariant:
#
#   1. Every `[RequiresDockerAndOptInFact]` method in the repo is NAMED in the E2E workflow.
#   2. The E2E workflow actually SETS MAINGUARD_VERIFY_E2E=1 — otherwise it would name the tests
#      and still skip them, which is the "looks applied but isn't" shape this repo keeps producing
#      and is precisely how #337 appeared to have closed this ticket without closing it.
#   3. No name in the workflow's filter is STALE (refers to a method that no longer exists), since a
#      renamed test would otherwise leave the filter matching nothing and the job passing on zero.
#   4. It found at least one opt-in test at all — a vacuous scan is a failure, not a pass.
#
# WHAT THIS GUARD DOES NOT DO
# ---------------------------
# It does not prove the workflow's schedule fires, and it cannot: GitHub honours `schedule` only for
# workflows on the DEFAULT branch. That is covered instead by the workflow's own `pull_request` path
# trigger, which includes the workflow file itself — so changing the gate runs the gate.
#
# Usage:  build/ci/verify-e2e-optin-coverage.sh
# Exit:   0 = every opt-in test is wired, 1 = a gap, 2 = the guard could not scan.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

ATTRIBUTE="RequiresDockerAndOptInFact"
OPT_IN_VAR="MAINGUARD_VERIFY_E2E"
WORKFLOW=".github/workflows/e2e-verification.yml"

# --- preconditions -----------------------------------------------------------------------------
if [ ! -f "$WORKFLOW" ]; then
  echo "::error::$WORKFLOW does not exist — the opt-in E2E tests have no job that runs them. If the workflow was renamed, update WORKFLOW in $0." >&2
  exit 2
fi

# The attribute must still exist as a type, or this guard is scanning for a construct the codebase
# has dropped and would report "all wired" having matched nothing.
if ! grep -rqI --include='*.cs' "class ${ATTRIBUTE}Attribute" -- Mainguard.Server.Tests; then
  echo "::error::no '${ATTRIBUTE}Attribute' type found — the opt-in mechanism moved or was removed, and this guard no longer understands the tree." >&2
  exit 2
fi

# --- 1. enumerate the opt-in tests -------------------------------------------------------------
# The attribute sits on its own line directly above the method signature, house style throughout the
# suite. awk carries the flag forward so intervening blank/comment lines do not break the pairing.
TESTS="$(grep -rhI --include='*.cs' -A6 "^[[:space:]]*\[${ATTRIBUTE}\]" -- . \
  | awk -v attr="\\\\[${ATTRIBUTE}\\\\]" '
      $0 ~ attr { armed = 1; next }
      armed && /public .*[[:space:]]([A-Za-z0-9_]+)\(/ {
        line = $0
        sub(/\(.*$/, "", line)           # drop the parameter list
        n = split(line, parts, /[[:space:]]+/)
        print parts[n]                   # the token immediately before "(" is the method name
        armed = 0
      }' | sort -u)"

if [ -z "$TESTS" ]; then
  echo "::error::found 0 methods marked [$ATTRIBUTE] — either they were all removed (then delete this guard and $WORKFLOW) or the scan is broken. A vacuous scan is a failure, not a pass." >&2
  exit 2
fi

COUNT="$(printf '%s\n' "$TESTS" | wc -l | tr -d ' ')"
echo "==> $COUNT opt-in E2E test(s) marked [$ATTRIBUTE]:"
printf '      %s\n' $TESTS

# --- 2. the workflow must actually opt in ------------------------------------------------------
# Naming the tests is worthless if the variable that ungates them is never set.
if ! grep -qE "^[[:space:]]*${OPT_IN_VAR}:[[:space:]]*[\"']?1[\"']?[[:space:]]*$" "$WORKFLOW"; then
  echo "::error::$WORKFLOW never sets ${OPT_IN_VAR}=1, so every test it names would SKIP. Naming a test in a job that cannot run it is how ticket #65 was closed once without being fixed." >&2
  exit 1
fi
echo "  [ok] $WORKFLOW sets ${OPT_IN_VAR}=1"

# --- 3 & 4. the workflow's FILTER and the set of opt-in tests must be EQUAL --------------------
# Deliberately compared against the `--filter` clauses only, not against the file's text. A plain
# `grep -F "$name" "$WORKFLOW"` was the first cut and it was wrong in a way worth recording: this
# workflow NAMES both tests in its header comment, so a test mentioned in prose and absent from the
# filter read as "wired". A guard that a comment can satisfy is the same species of defect as the
# one it was written to catch.
FILTER_NAMES="$(grep -oE 'FullyQualifiedName~[A-Za-z0-9_.]+' "$WORKFLOW" \
  | sed 's/^FullyQualifiedName~//' | awk -F. '{ print $NF }' | sort -u)"

if [ -z "$FILTER_NAMES" ]; then
  echo "::error::$WORKFLOW contains no 'FullyQualifiedName~' filter clauses — it runs nothing specific, so it cannot be the job that covers the opt-in tests." >&2
  exit 1
fi

MISSING=0

# Forward: an opt-in test the workflow does not select can never fail.
for t in $TESTS; do
  if printf '%s\n' $FILTER_NAMES | grep -qx -- "$t"; then
    echo "  [wired]   $t"
  else
    echo "::error::the opt-in test '$t' is not in $WORKFLOW's --filter — it is gated behind ${OPT_IN_VAR}=1 and NOTHING sets that for it, so it can never fail. Add it to the --filter, or delete the test."
    MISSING=1
  fi
done

# Reverse: a renamed method leaves a filter clause matching nothing, and `dotnet test` is perfectly
# happy to run zero tests and exit 0.
for name in $FILTER_NAMES; do
  if ! printf '%s\n' $TESTS | grep -qx -- "$name"; then
    echo "::error::$WORKFLOW filters on '$name', which is not a [$ATTRIBUTE] test in this tree — a stale filter clause matches nothing and the job would pass having run less than it claims."
    MISSING=1
  fi
done

if [ "$MISSING" -ne 0 ]; then
  exit 1
fi

echo "All $COUNT opt-in E2E test(s) are wired into $WORKFLOW, which opts in and has no stale filters."
