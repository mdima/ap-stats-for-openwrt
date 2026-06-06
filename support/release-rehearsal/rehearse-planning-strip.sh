#!/usr/bin/env bash
# Plan 22-04 — `.planning/` history-strip REHEARSAL (local, throwaway clone only).
#
# Purpose:
#   Prove the one-time `git filter-repo --invert-paths --path .planning/` strip
#   mechanics + the T2 leak-verification gate NOW, while nothing real is touched.
#   The REAL strip + cutover execute at the Phase 23 close (D-01 cutover model,
#   D-03 strip-deferred-to-Phase-23). This script de-risks that irreversible
#   rewrite by exercising the exact invocation + leak gate on a FRESH clone.
#
# Decisions honoured:
#   - D-01  After the strip, filtered history becomes THE canonical history (Phase 23).
#   - D-03  The strip itself is DEFERRED to the Phase 23 close — this is the rehearsal.
#   - D-04  Throwaway-proof: the rewrite runs ONLY inside a disposable fresh clone.
#   - T2    Leak driver: `.planning/` UAT/debug logs carry the operator's real lab-network
#           fingerprint (internal 192.168.1.x topology + real device MACs). The strip
#           must remove it from rewritten history; this script PROVES that removal.
#
# Determinism:
#   git-filter-repo is PINNED to 2.47.0 so the rewritten SHAs produced by this
#   Phase 22 rehearsal are reproducible against the Phase 23 real strip (T-22-10).
#
# SAFETY (Pitfall 2 / T-22-09):
#   filter-repo runs ONLY inside a `git clone --no-local` throwaway dir. It NEVER
#   targets the working tree / `milestone/v1.0`. filter-repo itself also refuses a
#   non-fresh clone as a second guard. The scratch clone is removed on exit (trap).
#   This script does NOT modify `.gitignore` (D-02 defers that to the Phase 23 cutover;
#   adding it now would break GSD `commit_docs` mid-milestone — RESEARCH Open Q #3).
#
# Usage:
#   bash support/release-rehearsal/rehearse-planning-strip.sh
#   # Optional: pass the source repo root explicitly (defaults to the current repo's toplevel):
#   bash support/release-rehearsal/rehearse-planning-strip.sh /path/to/repo

set -euo pipefail

# ---- Expected post-strip release tags (T2: 9 historical tags survive, re-pointed) ----
EXPECTED_TAGS=(v0.9 v0.9.1 v0.9.2 v0.10 v0.11 v0.11.1 v0.12 v0.13 v0.14)
EXPECTED_TAG_COUNT=${#EXPECTED_TAGS[@]}

# ---- Resolve the SOURCE repo (the thing we clone FROM — never rewritten) ----
SRC_REPO="${1:-}"
if [[ -z "$SRC_REPO" ]]; then
  SRC_REPO="$(git rev-parse --show-toplevel 2>/dev/null || true)"
fi
if [[ -z "$SRC_REPO" || ! -e "$SRC_REPO/.git" ]]; then
  echo "FATAL: source repo not found (resolved to '${SRC_REPO:-<empty>}'). Run from inside the repo, or pass the repo root as \$1." >&2
  exit 1
fi

# ---- Pinned tool guard (T-22-10 / T-22-SC: the one sanctioned pip install) ----
# NOTE: `git filter-repo --version` reports filter-repo's INTERNAL git-describe string
# (e.g. a hash), NOT the PyPI package version. The authoritative pin (2.47.0) is the
# PyPI package version, so we assert THAT via pip — not by parsing --version output.
REQUIRED_FR_VERSION="2.47.0"
FR_PKG_VERSION="$(pip show git-filter-repo 2>/dev/null | awk -F': ' '/^Version:/{print $2}' | tr -d '[:space:]' || true)"
if [[ -z "$FR_PKG_VERSION" ]]; then
  echo "FATAL: git-filter-repo is not installed. Install the PINNED version with:" >&2
  echo "       pip install git-filter-repo==${REQUIRED_FR_VERSION}" >&2
  exit 1
fi
if [[ "$FR_PKG_VERSION" != "$REQUIRED_FR_VERSION" ]]; then
  echo "FATAL: git-filter-repo $FR_PKG_VERSION found, but ${REQUIRED_FR_VERSION} is required for Phase 22<->23 SHA determinism." >&2
  echo "       pip install git-filter-repo==${REQUIRED_FR_VERSION}" >&2
  exit 1
fi

# ---- Resolve a portable filter-repo invocation ----
# Prefer the `git filter-repo` subcommand if git can dispatch it; otherwise fall back
# to `python -m git_filter_repo` (the importable module — robust on Windows/MSYS where
# the `git-filter-repo` script may not be on PATH for git's subcommand dispatch).
run_filter_repo() {
  if git filter-repo --version >/dev/null 2>&1; then
    git filter-repo "$@"
  elif python -m git_filter_repo --version >/dev/null 2>&1; then
    python -m git_filter_repo "$@"
  else
    echo "FATAL: cannot invoke git-filter-repo (neither 'git filter-repo' nor 'python -m git_filter_repo' runs)." >&2
    return 1
  fi
}

# ---- Fresh, isolated scratch dir (Pitfall 2: NEVER the working tree) ----
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/planning-strip-rehearsal.XXXXXX")"
if [[ -z "$SCRATCH" || ! -d "$SCRATCH" ]]; then
  echo "FATAL: could not create a scratch temp dir for the throwaway clone." >&2
  exit 1
fi
# Always clean up the throwaway clone — pass or fail (T-22-09: no leftover rewritten repo).
cleanup() { rm -rf "$SCRATCH"; }
trap cleanup EXIT

CLONE="$SCRATCH/strip-rehearsal"

echo "=================================================================="
echo " .planning/ STRIP REHEARSAL  (Plan 22-04 / D-03 / D-04)"
echo "   source repo : $SRC_REPO   (READ-ONLY — never rewritten)"
echo "   scratch dir : $SCRATCH    (throwaway — removed on exit)"
echo "   filter-repo : $FR_PKG_VERSION (pinned, PyPI package version)"
echo "=================================================================="

# ---- 1) FRESH clone (Pitfall 2) ----
echo ""
echo "[1/5] Fresh clone (git clone --no-local) ..."
git clone --no-local "$SRC_REPO" "$CLONE"

# Sanity: the fresh clone must actually contain .planning/ history + the 9 tags,
# otherwise the rehearsal proves nothing.
PRE_PLANNING_COMMITS="$(git -C "$CLONE" log --all --oneline -- .planning/ | wc -l | tr -d ' ')"
PRE_TAG_COUNT="$(git -C "$CLONE" tag --list | wc -l | tr -d ' ')"
echo "      pre-strip: .planning/ touched in $PRE_PLANNING_COMMITS commit(s); $PRE_TAG_COUNT tag(s) present."
if [[ "$PRE_PLANNING_COMMITS" -eq 0 ]]; then
  echo "FATAL: the fresh clone has NO .planning/ history — nothing to strip; rehearsal is meaningless." >&2
  exit 1
fi
if [[ "$PRE_TAG_COUNT" -ne "$EXPECTED_TAG_COUNT" ]]; then
  echo "FATAL: expected $EXPECTED_TAG_COUNT tags in the fresh clone, found $PRE_TAG_COUNT." >&2
  exit 1
fi

# ---- 2) The strip (Pitfall 3: trailing-slash subtree, invert keeps everything else) ----
# IMPORTANT (Phase 22 rehearsal finding — REQUIRED for Phase 23):
#   `--path .planning/` matches a path PREFIX FROM THE REPO ROOT, so it removes the
#   top-level .planning/ only. History also contains a NESTED .planning/ under an old
#   GSD worktree path (.claude/worktrees/<id>/.planning/...) that carries the SAME T2
#   lab fingerprint (192.168.22.0/24). A bare `--path .planning/` would leave that nested
#   copy in public history. We therefore ALSO pass `--path-glob '*/.planning/*'` so EVERY
#   .planning/ subtree — root and nested — is stripped. The leak gate below (step 4)
#   asserts this is complete; the Phase 23 real strip MUST use the same two selectors.
echo ""
echo "[2/5] git filter-repo --invert-paths --path .planning/ --path-glob '*/.planning/*'  (throwaway clone only) ..."
# filter-repo rewrites the repo of the CWD; cd into the throwaway clone first (Pitfall 2).
# Run in a subshell so the cd does not leak; the wrapper picks git-subcommand vs python -m.
( cd "$CLONE" && run_filter_repo --invert-paths --path .planning/ --path-glob '*/.planning/*' )

# ===================== LEAK-VERIFICATION GATE (T2 / Pitfall 3) =====================
# Each assertion fails LOUD (exit 1) on violation.
RC=0

# ---- 3) No .planning/ blob survives anywhere in rewritten history ----
echo ""
echo "[3/5] Assert: no .planning/ blob survives in rewritten history ..."
PLANNING_HITS="$(git -C "$CLONE" log --all --oneline -- .planning/ | wc -l | tr -d ' ')"
if [[ "$PLANNING_HITS" -ne 0 ]]; then
  echo "  FAIL: .planning/ still referenced in $PLANNING_HITS commit(s) of rewritten history." >&2
  git -C "$CLONE" log --all --oneline -- .planning/ | head -10 >&2
  RC=1
else
  echo "  PASS: git log --all -- .planning/ is EMPTY (no .planning/ blob survives)."
fi

# ---- 4) T2 lab-network fingerprint gone (scoped to .planning/ paths) ----
# NOTE (scoping rationale — see SUMMARY deviation): the T2 leak driver is the lab
# fingerprint *inside .planning/ UAT/debug logs*. The SAME RFC-1918 example IPs
# (192.168.1.x / 192.168.22.x) ALSO appear, BY DESIGN, in non-.planning files — test
# fixtures (redacted lab captures kept verbatim per support/redact-fixtures policy),
# source defaults, and docs. Those are intentionally public and SURVIVE the strip.
# A blanket whole-tree grep would therefore ALWAYS match legitimate, kept content and
# is not the T2 driver. We assert the driver precisely: no 192.168. survives in any
# .planning/-pathed blob across ALL rewritten refs.
echo ""
echo "[4/5] Assert: T2 lab-network fingerprint (192.168.) absent from rewritten .planning/ history ..."
# NOTE: `git grep` exits 1 when it finds NO match. Under `set -euo pipefail` that would
# abort the script on the (desired) clean case — so capture with a no-match-safe guard.
ALL_REVS="$(git -C "$CLONE" rev-list --all)"
FINGERPRINT_OUT="$(git -C "$CLONE" grep -nI -e '192\.168\.' $ALL_REVS -- '*.planning/*' '.planning/*' 2>/dev/null || true)"
FINGERPRINT_HITS="$(printf '%s' "$FINGERPRINT_OUT" | grep -c . || true)"
if [[ "$FINGERPRINT_HITS" -ne 0 ]]; then
  echo "  FAIL: T2 leak driver SURVIVED — $FINGERPRINT_HITS '192.168.' match(es) remain in .planning/-pathed blobs." >&2
  printf '%s\n' "$FINGERPRINT_OUT" | head -10 >&2
  RC=1
else
  echo "  PASS: no '192.168.' lab fingerprint remains in any .planning/-pathed blob (T2 driver verified clean)."
fi

# ---- 5) All 9 release tags still resolve (to new rewritten SHAs) ----
echo ""
echo "[5/5] Assert: all $EXPECTED_TAG_COUNT release tags still resolve (re-pointed to rewritten commits) ..."
POST_TAG_COUNT="$(git -C "$CLONE" tag --list | wc -l | tr -d ' ')"
MISSING=()
for t in "${EXPECTED_TAGS[@]}"; do
  if ! git -C "$CLONE" rev-parse -q --verify "refs/tags/$t" >/dev/null 2>&1; then
    MISSING+=("$t")
  fi
done
if [[ "$POST_TAG_COUNT" -ne "$EXPECTED_TAG_COUNT" || ${#MISSING[@]} -ne 0 ]]; then
  echo "  FAIL: expected $EXPECTED_TAG_COUNT tags, found $POST_TAG_COUNT; missing: ${MISSING[*]:-none}." >&2
  RC=1
else
  echo "  PASS: all $EXPECTED_TAG_COUNT tags resolve post-strip."
fi

# ---- Capture rewritten SHAs for Phase 23 determinism comparison (T-22-10) ----
echo ""
echo "------------------------------------------------------------------"
echo " Rewritten-SHA snapshot (Phase 23 determinism baseline):"
MAIN_TIP="$(git -C "$CLONE" rev-parse HEAD 2>/dev/null || echo '<unknown>')"
echo "   main-tip (HEAD): $MAIN_TIP"
for t in "${EXPECTED_TAGS[@]}"; do
  SHA="$(git -C "$CLONE" rev-list -n1 "$t" 2>/dev/null || echo '<missing>')"
  printf "   %-10s %s\n" "$t" "$SHA"
done
echo "------------------------------------------------------------------"

if [[ "$RC" -ne 0 ]]; then
  echo ""
  echo "RESULT: REHEARSAL FAILED — one or more leak-gate assertions did not pass (see above)." >&2
  exit 1
fi

echo ""
echo "RESULT: REHEARSAL PASSED — strip mechanics + leak gate proven on the throwaway clone."
echo ""
echo "VERIFY: re-run any of these against the THROWAWAY clone (NOT the working tree); each must hold:"
echo "  1. git log --all --oneline -- .planning/                                  # expect: 0 lines (.planning/ gone)"
echo "  2. git grep -nI -e '192\\.168\\.' \$(git rev-list --all) -- '.planning/*'    # expect: 0 lines (T2 driver clean)"
echo "  3. git tag --list | wc -l                                                  # expect: ${EXPECTED_TAG_COUNT} (all release tags re-pointed)"
