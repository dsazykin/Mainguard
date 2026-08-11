#!/usr/bin/env bash
# =============================================================================
# Ticket #66 — docs/repo-map/ is meant to be COMPLETE. This makes that testable.
# =============================================================================
# CLAUDE.md/AGENTS.md say an unindexed file is an incomplete change. Nothing checked it, so the index
# drifted: `Mainguard.Server.Tests/DataRootIsolationTests.cs` — which holds the assertion that stops a
# new daemon store path from ignoring its session token and making every concurrent in-proc test host
# share one file — had no entry at all. It was not alone.
#
# WHY AN ALLOWLIST RATHER THAN A CLEAN GATE
# -----------------------------------------
# The first full sweep found a large backlog of already-unindexed files. Failing CI on all of them
# would either block every PR or, far more likely, get the whole check deleted within a week. So this
# borrows the shape the P2-04 conformance suites already use in this repo (known-failures.txt +
# the allowlist-shrink-guard job): the backlog is WRITTEN DOWN in
# docs/repo-map/known-unindexed.txt, and the rule is that the list may only SHRINK.
#
# The practical effect, which is the whole point:
#   * a NEW file with no repo-map entry fails immediately — the rule is enforced from now on;
#   * the existing backlog is visible, counted, and can only go down;
#   * nobody can quietly re-add a file to the backlog to make their PR pass, because growth fails
#     in the paired shrink-only CI job.
#
# HOW "INDEXED" IS DECIDED, AND WHY IT IS DELIBERATELY GENEROUS
# ------------------------------------------------------------
# The map is prose with nested bullets, not a machine-readable table, and it legitimately names things
# three different ways: a full backticked path, a bare type name without `.cs`, or a directory standing
# in for its contents. A strict matcher would report hundreds of false gaps — a sweep on `main` did
# exactly that, reporting 291 "unindexed" files, because `main` indexes by directory prose rather than
# per file. So a file counts as indexed when ANY of these appears anywhere in docs/repo-map/*.md:
#   * its path or filename,
#   * its basename with `.cs` / `.axaml.cs` stripped (the map often names the TYPE),
#   * any type it declares (filename and type name diverge often enough to matter),
#   * a directory entry covering it (e.g. EF `Migrations/`).
# Generous means this check UNDER-reports. That is the correct direction for a gate: every file it
# does flag is a real gap, and it can never fail a PR over a formatting nicety.
#
# Usage:  build/ci/verify-repo-map-complete.sh [--write-allowlist]
# Exit:   0 = no unindexed file outside the allowlist, 1 = a gap, 2 = the guard could not scan.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

MAP_DIR="docs/repo-map"
ALLOWLIST="$MAP_DIR/known-unindexed.txt"
WRITE_MODE="${1:-}"

# Scratch project trees CLAUDE.md excludes from the solution, plus generated EF output the map covers
# with a single `Migrations/` directory entry.
is_excluded() {
  case "$1" in
    Mainguard.StyleConsole/*|Mainguard.StyleTests/*) return 0 ;;
    */Migrations/*) return 0 ;;
    *.Designer.cs) return 0 ;;
    *) return 1 ;;
  esac
}

# --- preconditions -----------------------------------------------------------------------------
if [ ! -d "$MAP_DIR" ]; then
  echo "::error::$MAP_DIR does not exist — the per-file index moved or this is the wrong branch. (On main the map is prose-by-directory and this guard does not apply.)" >&2
  exit 2
fi

MAP_FILES=$(find "$MAP_DIR" -name '*.md' | sort)
if [ -z "$MAP_FILES" ]; then
  echo "::error::$MAP_DIR contains no .md files — a vacuous scan is a failure, not a pass." >&2
  exit 2
fi

# One corpus, materialised ONCE into files rather than shell variables. The map is ~570 KB and passing
# that through `printf '%s' "$var" | grep` silently produced an EMPTY match set (argument-length
# limits) — i.e. the guard reported every file as unindexed while looking at nothing. The positive
# control below is what caught it, and is why it exists.
#
# The second copy has ALL whitespace collapsed out, which is how a name that the map's ~100-column
# hard wrap split across two lines is still found.
CORPUS_FILE="$(mktemp)"
CORPUS_FLAT_FILE="$(mktemp)"
trap 'rm -f "$CORPUS_FILE" "$CORPUS_FLAT_FILE"' EXIT
cat $MAP_FILES > "$CORPUS_FILE"
tr -d '[:space:]' < "$CORPUS_FILE" > "$CORPUS_FLAT_FILE"

mapfile -t ALL_CS < <(git ls-files '*.cs')
if [ "${#ALL_CS[@]}" -eq 0 ]; then
  echo "::error::git ls-files '*.cs' returned nothing — vacuous scan." >&2
  exit 2
fi

# Positive control: the guard must be able to FIND a file it knows is indexed. Without this, a broken
# matcher would report every file as a gap, or (worse, after allowlisting) as fine.
if ! grep -qF 'MainguardPathsGuardTests' "$CORPUS_FILE"; then
  echo "::error::positive control failed — 'MainguardPathsGuardTests' is not in $MAP_DIR, so the corpus was not read correctly." >&2
  exit 2
fi

# --- the sweep ---------------------------------------------------------------------------------
UNINDEXED=()
for f in "${ALL_CS[@]}"; do
  is_excluded "$f" && continue

  base="$(basename "$f")"
  stem="${base%.cs}"
  stem="${stem%.axaml}"        # Views are written `X.axaml(.cs)` or bare `X`

  if grep -qF -- "$base" "$CORPUS_FILE"; then continue; fi
  if grep -qF -- "$stem" "$CORPUS_FILE"; then continue; fi
  if grep -qF -- "$stem" "$CORPUS_FLAT_FILE"; then continue; fi

  # Filename and type name diverge often enough that skipping this produces false gaps.
  types="$(grep -hoE '^[[:space:]]*(public|internal|sealed|abstract|static|partial|[[:space:]])*(class|record|interface|enum|struct)[[:space:]]+[A-Za-z0-9_]+' "$f" 2>/dev/null \
    | awk '{ print $NF }' | sort -u || true)"
  found=0
  for t in $types; do
    if grep -qF -- "$t" "$CORPUS_FILE"; then found=1; break; fi
  done
  [ "$found" -eq 1 ] && continue

  UNINDEXED+=("$f")
done

printf '%s\n' "${UNINDEXED[@]:-}" | sed '/^$/d' | sort > "$REPO_ROOT/.repo-map-unindexed.tmp"
FOUND_COUNT=$(wc -l < "$REPO_ROOT/.repo-map-unindexed.tmp" | tr -d ' ')

if [ "$WRITE_MODE" = "--write-allowlist" ]; then
  {
    echo "# Files with no docs/repo-map/ entry, recorded by build/ci/verify-repo-map-complete.sh."
    echo "#"
    echo "# THIS LIST MAY ONLY SHRINK (ticket #66). It is the backlog that existed when the"
    echo "# completeness guard was introduced, not a licence: a NEW unindexed file fails CI, and the"
    echo "# paired shrink-only job fails any PR that adds a line here. Delete a line by writing the"
    echo "# file's entry into the matching docs/repo-map/*.md — that is the whole intended workflow."
    echo "#"
    echo "# Generated: build/ci/verify-repo-map-complete.sh --write-allowlist"
    cat "$REPO_ROOT/.repo-map-unindexed.tmp"
  } > "$ALLOWLIST"
  rm -f "$REPO_ROOT/.repo-map-unindexed.tmp"
  echo "Wrote $ALLOWLIST with $FOUND_COUNT entries."
  exit 0
fi

if [ ! -f "$ALLOWLIST" ]; then
  echo "::error::$ALLOWLIST is missing — regenerate it with: build/ci/verify-repo-map-complete.sh --write-allowlist" >&2
  rm -f "$REPO_ROOT/.repo-map-unindexed.tmp"
  exit 2
fi

grep -vE '^[[:space:]]*(#|$)' "$ALLOWLIST" | sort > "$REPO_ROOT/.repo-map-allowed.tmp"
ALLOWED_COUNT=$(wc -l < "$REPO_ROOT/.repo-map-allowed.tmp" | tr -d ' ')

# New gaps: unindexed now, not on the recorded backlog.
NEW_GAPS="$(comm -23 "$REPO_ROOT/.repo-map-unindexed.tmp" "$REPO_ROOT/.repo-map-allowed.tmp")"
# Stale allowlist lines: recorded as a gap but now indexed (or deleted). Not a failure — it means
# someone did the work — but it must be reported, or the list stops shrinking on paper.
FIXED="$(comm -13 "$REPO_ROOT/.repo-map-unindexed.tmp" "$REPO_ROOT/.repo-map-allowed.tmp")"

rm -f "$REPO_ROOT/.repo-map-unindexed.tmp" "$REPO_ROOT/.repo-map-allowed.tmp"

echo "==> ${#ALL_CS[@]} tracked .cs files; $FOUND_COUNT unindexed; $ALLOWED_COUNT on the recorded backlog."

if [ -n "$FIXED" ]; then
  echo "  These are now indexed (or gone) and their allowlist lines should be deleted:"
  printf '      %s\n' $FIXED
fi

if [ -n "$NEW_GAPS" ]; then
  echo "::error::the following file(s) have no docs/repo-map/ entry. AGENTS.md: an unindexed file is an incomplete change — add each to the matching docs/repo-map/*.md in THIS change."
  printf '::error::  %s\n' $NEW_GAPS
  exit 1
fi

echo "No unindexed files outside the recorded backlog."
