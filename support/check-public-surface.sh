#!/usr/bin/env bash
# Public-surface leak guard — asserts the files that become public at release
# carry NO internal-workflow literals.
#
# Purpose:
#   Everything in the "public surface" set (README, docs/, CONTRIBUTING, SECURITY,
#   issue templates) is pushed to the public repository at the release cutover.
#   Any internal planning-workflow literal that survives into one of those files
#   leaks permanently into public history. This guard fails LOUD if any of the
#   three forbidden literals appears in a present public-surface file, so it can
#   be sampled per docs task and run as the pre-cutover public-surface gate.
#
# Forbidden literals (the internal planning workflow must never surface publicly):
#   - .planning            (the private planning directory)
#   - get-shit-done        (the internal planning workflow name)
#   - GSD                   (its abbreviation)
#
# Scope:
#   Working-tree grep over the present public-surface files ONLY (NOT a rev-list
#   history grep — that is the separate one-time cutover leak gate in
#   support/release-rehearsal/). New markdown lives in the working tree, so a
#   per-file working-tree scan is sufficient and fast.
#
# Tolerance:
#   CONTRIBUTING.md / SECURITY.md / .github/ISSUE_TEMPLATE/ land in a later plan.
#   The guard MUST NOT error on their absence — it only fails on a literal match
#   inside a file that is actually present.
#
# ARG_MAX safety (mirrors the WR-06 leak-grep pattern in
#   support/release-rehearsal/rehearse-planning-strip.sh):
#   The target set is collected into a bounded array and grepped in fixed-size
#   batches, so the argument list can never blow ARG_MAX even if docs/ grows
#   large. git grep exit codes are distinguished per batch: 0 = match (leak),
#   1 = no match (clean), >1 = real error (ABORT — never silently report PASS).
#
# Usage:
#   bash support/check-public-surface.sh
#   exit 0 = clean (one-line PASS); exit non-zero = leak found or a grep error.

set -euo pipefail

# ---- The three forbidden literals (fixed strings, matched literally) ----
FORBIDDEN=(".planning" "get-shit-done" "GSD")

# ---- Resolve repo root so the guard works from any CWD ----
REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "FATAL: not inside a git working tree — cannot resolve the public-surface set." >&2
  exit 2
fi
cd "$REPO_ROOT"

# ---- Build the present public-surface file set (skip what does not yet exist) ----
# Fixed single-file members; directory members are expanded to their present files.
FILES=()

for f in README.md CONTRIBUTING.md SECURITY.md; do
  [[ -f "$f" ]] && FILES+=("$f")
done

# docs/ — every present file under the tree (markdown today; tolerate any).
if [[ -d docs ]]; then
  while IFS= read -r -d '' df; do
    FILES+=("$df")
  done < <(find docs -type f -print0)
fi

# .github/ISSUE_TEMPLATE/ — present files only (lands in a later plan; tolerate absence).
if [[ -d .github/ISSUE_TEMPLATE ]]; then
  while IFS= read -r -d '' tf; do
    FILES+=("$tf")
  done < <(find .github/ISSUE_TEMPLATE -type f -print0)
fi

if [[ ${#FILES[@]} -eq 0 ]]; then
  echo "PASS: no public-surface files present yet — nothing to scan." >&2
  echo "PASS: public-surface clean (0 files scanned)."
  exit 0
fi

# ---- Batched, fail-loud grep over the present files (ARG_MAX-safe) ----
# Worst per-batch outcome wins: 0 (leak) dominates clean; >1 (error) dominates all.
BATCH=200
worst=1                 # 1 = clean default; promoted to 0 (leak) or >1 (error)
LEAK_OUT="$(mktemp 2>/dev/null || echo "${TMPDIR:-/tmp}/check-public-surface.$$")"
: > "$LEAK_OUT"
cleanup() { rm -f "$LEAK_OUT"; }
trap cleanup EXIT

# Assemble the -e args once (three fixed strings).
GREP_PATTERNS=()
for lit in "${FORBIDDEN[@]}"; do
  GREP_PATTERNS+=(-e "$lit")
done

i=0
n=${#FILES[@]}
while [[ $i -lt $n ]]; do
  batch=("${FILES[@]:i:BATCH}")
  # -F fixed-string, -n line number, -H always show filename (even for one file),
  # -I skip binaries (e.g. a committed screenshot under docs/img/).
  if grep -F -n -H -I "${GREP_PATTERNS[@]}" "${batch[@]}" >> "$LEAK_OUT" 2>/dev/null; then
    st=0
  else
    st=$?
  fi
  if [[ $st -gt 1 ]]; then
    worst=$st           # real grep error — abort below, never PASS
    break
  elif [[ $st -eq 0 ]]; then
    worst=0             # at least one literal matched somewhere => leak
  fi
  i=$((i + BATCH))
done

if [[ "$worst" -gt 1 ]]; then
  echo "FATAL: leak grep ERRORED (grep exit $worst) — the gate did NOT run to completion; refusing to report PASS." >&2
  exit "$worst"
elif [[ "$worst" -eq 0 ]]; then
  HITS="$(wc -l < "$LEAK_OUT" | tr -d ' ')"
  echo "FAIL: public-surface leak — $HITS forbidden literal match(es) (.planning / get-shit-done / GSD):" >&2
  cat "$LEAK_OUT" >&2
  exit 1
fi

echo "PASS: public-surface clean — 0 forbidden literals in ${#FILES[@]} file(s)."
exit 0
