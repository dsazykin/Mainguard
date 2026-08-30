#!/usr/bin/env bash
# Mutation harness: break ONE guard, prove the named test goes red, restore it, prove green again.
#
# It exists because of a specific way this discipline lies to you. Restoring a mutated file with `mv`
# (or `git checkout --` alone) preserves the file's mtime, so MSBuild's up-to-date check skips the
# rebuild and the "restored" run executes the MUTATED assembly — a mutation pass reporting 0 red while
# testing exactly the binary it thought it had thrown away. So:
#
#   1. every write to a source file is followed by `touch`, and
#   2. the run REFUSES TO REPORT unless the built dll's mtime actually moved.
#
# Rule 2 is the load-bearing one: rule 1 is a habit, and a habit that is silently skipped produces the
# same green as a habit that was followed.
#
# Usage:  build/mutate.sh <label> <file> <python-expr-file> <test-project> <filter>
# Simpler: it is driven by build/mutations/<label>.py, which reads the source on stdin and writes the
# mutated source on stdout. See the header of any of those files.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LABEL="${1:?label}"
FILE="${2:?source file}"
PATCH="${3:?mutation script}"
PROJECT="${4:?test project}"
FILTER="${5:?test filter}"

# The assembly to watch is the one that CONTAINS the mutated code, as the test host will load it —
# i.e. the owning project's dll inside the TEST project's output directory. Watching the test
# assembly instead is the mistake this harness exists to catch, and it caught it on its own first run:
# mutating Mainguard.Agents rebuilt Mainguard.Agents.dll while Mainguard.Tests.dll legitimately stayed
# up to date, so a green restore would have been reported off an unchanged test dll while the thing
# under test had in fact been rebuilt. One is the wrong question; this is the right one.
OWNER="${FILE%%/*}"
ASSEMBLY="$ROOT/$PROJECT/bin/Release/net10.0/$OWNER.dll"

mtime_of() { stat -f %m "$1" 2>/dev/null || stat -c %Y "$1"; }

run_tests() {
  local before after
  before="$(mtime_of "$ASSEMBLY" 2>/dev/null || echo 0)"
  # `dotnet test` builds first; capture the result but never let a red test kill the script, because
  # red is the expected outcome of half the runs here.
  set +e
  dotnet test "$ROOT/$PROJECT" -c Release --filter "$FILTER" > "$ROOT/.mutate-$LABEL.log" 2>&1
  local code=$?
  set -e
  after="$(mtime_of "$ASSEMBLY")"

  if [ "$before" = "$after" ]; then
    echo "REFUSING TO REPORT: $ASSEMBLY did not rebuild (mtime $before unchanged)."
    echo "The run above tested a STALE assembly, so it is evidence about nothing."
    exit 99
  fi

  return $code
}

echo "== mutation $LABEL =="
cp "$ROOT/$FILE" "$ROOT/.mutate-$LABEL.orig"
python3 "$ROOT/$PATCH" < "$ROOT/.mutate-$LABEL.orig" > "$ROOT/$FILE"
touch "$ROOT/$FILE"
if cmp -s "$ROOT/$FILE" "$ROOT/.mutate-$LABEL.orig"; then
  echo "MUTATION DID NOT APPLY: $PATCH changed nothing in $FILE."
  cp "$ROOT/.mutate-$LABEL.orig" "$ROOT/$FILE"; touch "$ROOT/$FILE"
  exit 98
fi

if run_tests; then
  echo "$LABEL: STILL GREEN — the guard is not covered."
  cp "$ROOT/.mutate-$LABEL.orig" "$ROOT/$FILE"; touch "$ROOT/$FILE"
  rm -f "$ROOT/.mutate-$LABEL.orig"
  exit 1
fi
echo "$LABEL: went RED as it must —"
grep -E "^\s+(Failed|X) |error CS" "$ROOT/.mutate-$LABEL.log" | head -8 || true
grep -E "Failed!|Build FAILED" "$ROOT/.mutate-$LABEL.log" | head -2 || true

# Restore, and TOUCH — a restore that preserves mtime is how the next run tests the mutant.
cp "$ROOT/.mutate-$LABEL.orig" "$ROOT/$FILE"
touch "$ROOT/$FILE"
rm -f "$ROOT/.mutate-$LABEL.orig"

if run_tests; then
  echo "$LABEL: restored, green again."
  rm -f "$ROOT/.mutate-$LABEL.log"
else
  echo "$LABEL: RESTORE FAILED — still red after restore. Fix the tree before trusting anything above."
  exit 2
fi
