#!/usr/bin/env bash
#
# Smoke-drives the Memory Snapshot Data Tool end-to-end and leaves artifacts on disk:
#
#   build (Release) -> export a .snap to DuckDB -> summary -> HTML report -> screenshot
#
# This is the agent path for "run the tool" / "confirm a change works in the real app."
# The tool is a CLI whose real product is a rendered HTML report, so the script renders
# that report headless with Chrome and writes a PNG you can actually look at.
#
# A .snap snapshot is required and is NOT shipped in this repo (captures are large and
# user-specific). You must supply one — capture it from the Unity Memory Profiler, or
# point at one you already have.
#
# Usage:
#   .claude/skills/run-memory-snapshot-data-tool/smoke.sh <snapshot.snap>
#   MSDT_SNAP=/path/to/snapshot.snap   .claude/skills/run-memory-snapshot-data-tool/smoke.sh
#   MSDT_SNAP_DIR=/path/to/captures    .claude/skills/run-memory-snapshot-data-tool/smoke.sh   # picks the smallest .snap there
#
# Env overrides:
#   MSDT_SNAP        explicit path to a .snap (alternative to the positional argument)
#   MSDT_SNAP_DIR    dir to search for the smallest .snap when no path is given (no default)
#   MSDT_OUT_DIR     where artifacts land (default: /tmp/msdt-run)
#   MSDT_CONFIG      dotnet build configuration (default: Release)
#   MSDT_SQLITE=1    also exercise the SQLite backend (export + report).
#                    NOTE: the SQLite *report* query is very slow (~150s); DuckDB is ~0.1s.
#   MSDT_NO_BUILD=1  skip the build step (assume the solution is already built)
#
# Exit code 0 = every checked step passed.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

OUT_DIR="${MSDT_OUT_DIR:-/tmp/msdt-run}"
CONFIG="${MSDT_CONFIG:-Release}"
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
mkdir -p "$OUT_DIR"

fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

# ---- resolve snapshot (arg > $MSDT_SNAP > smallest under $MSDT_SNAP_DIR) ----
SNAP="${1:-${MSDT_SNAP:-}}"
if [[ -z "$SNAP" && -n "${MSDT_SNAP_DIR:-}" ]]; then
  step "Discovering smallest .snap under $MSDT_SNAP_DIR"
  SNAP="$(find "$MSDT_SNAP_DIR" -maxdepth 1 -name '*.snap' -type f 2>/dev/null | while read -r f; do
            printf '%s %s\n' "$(wc -c < "$f")" "$f"
          done | sort -n | head -1 | cut -d' ' -f2-)"
fi
[[ -n "$SNAP" && -f "$SNAP" ]] || fail "No .snap provided. Pass one as an argument, set MSDT_SNAP, or set MSDT_SNAP_DIR. Captures are large and live outside this repo (see SKILL.md)."
echo "Using snapshot: $SNAP"

# ---- build ----
if [[ "${MSDT_NO_BUILD:-}" != "1" ]]; then
  step "Building solution ($CONFIG)"
  dotnet build MemorySnapshotDataTools.sln -c "$CONFIG" >/dev/null || fail "build failed"
fi

# ---- locate the CLI (RID-specific output dir; glob avoids hard-coding osx-arm64) ----
CLI_DLL="$(find "Cli/bin/$CONFIG" -name MemorySnapshotDataTools.dll 2>/dev/null | head -1)"
[[ -n "$CLI_DLL" ]] || fail "CLI dll not found under Cli/bin/$CONFIG — run a build first (unset MSDT_NO_BUILD)."
run_cli() { dotnet "$CLI_DLL" "$@"; }

step "CLI help (sanity)"
run_cli --help >/dev/null || fail "--help failed"

# ---- export -> DuckDB ----
DB="$OUT_DIR/out.duckdb"
rm -f "$DB" "$DB.wal"
step "export -> $DB (DuckDB)"
run_cli export "$SNAP" "$DB" --validate minimal --verbose || fail "export failed"
[[ -s "$DB" ]] || fail "export produced no database file"

# ---- summary (no DB generated; reads the one we just made) ----
step "summary $DB"
run_cli summary "$DB" | tee "$OUT_DIR/summary.txt"
grep -q "Memory Usage Summary" "$OUT_DIR/summary.txt" || fail "summary output missing expected header"

# ---- report -> HTML ----
HTML="$OUT_DIR/report.html"
rm -f "$HTML"
step "report $DB -> $HTML"
run_cli report "$DB" --out "$HTML" --title "MSDT Smoke Report" --verbose || fail "report failed"
grep -q "<!DOCTYPE html>" "$HTML" || fail "report HTML does not look like HTML"

# ---- screenshot the rendered report ----
PNG="$OUT_DIR/report.png"
rm -f "$PNG"
if [[ -x "$CHROME" ]]; then
  step "screenshot $HTML -> $PNG"
  "$CHROME" --headless --disable-gpu --hide-scrollbars --window-size=1400,2400 \
    --screenshot="$PNG" "file://$HTML" >/dev/null 2>&1 || true
  if [[ -s "$PNG" ]]; then echo "screenshot OK: $PNG"; else echo "WARN: screenshot not produced (open $HTML manually)"; fi
else
  echo "WARN: Chrome not found at '$CHROME' — skipping screenshot. Open $HTML in a browser instead."
fi

# ---- optional SQLite backend coverage (slow report; off by default) ----
if [[ "${MSDT_SQLITE:-}" == "1" ]]; then
  SDB="$OUT_DIR/out.db"
  rm -f "$SDB" "$SDB-wal" "$SDB-shm"
  step "export -> $SDB (SQLite)"
  run_cli export "$SNAP" "$SDB" --destination sqlite --validate minimal || fail "sqlite export failed"
  step "report from SQLite -> $OUT_DIR/report-sqlite.html  (SLOW: query ~150s)"
  run_cli report "$SDB" --out "$OUT_DIR/report-sqlite.html" || fail "sqlite report failed"
fi

step "DONE — artifacts in $OUT_DIR"
ls -la "$OUT_DIR"
