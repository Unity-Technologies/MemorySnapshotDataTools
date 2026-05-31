#!/usr/bin/env bash
#
# Export a Unity memory snapshot and validate the export against a golden JSON
# produced by the Unity "Extract Golden Values" editor tool.
#
# Usage:
#   scripts/validate-golden.sh <snapshot.snap> <golden.json> [options]
#
# Options:
#   --destination <duckdb|sqlite>  Export backend (default: duckdb).
#   --db <path>                    Export database path (default: a temp file, deleted on exit).
#   --result <path>                Validation result JSON path (default: next to the golden file).
#   --keep-db                      Keep the exported database instead of deleting it.
#   --configuration <cfg>          dotnet build configuration (default: Release).
#
# Exit codes: 0 = validation passed, 1 = metric mismatch, 3 = export or validation error.

set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <snapshot.snap> <golden.json> [--destination duckdb|sqlite] [--db <path>] [--result <path>] [--keep-db] [--configuration <cfg>]" >&2
  exit 3
fi

SNAPSHOT="$1"
GOLDEN="$2"
shift 2

DESTINATION="duckdb"
DB_PATH=""
RESULT_PATH=""
KEEP_DB=0
CONFIGURATION="Release"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --destination) DESTINATION="$2"; shift 2 ;;
    --db)          DB_PATH="$2"; shift 2 ;;
    --result)      RESULT_PATH="$2"; shift 2 ;;
    --keep-db)     KEEP_DB=1; shift ;;
    --configuration) CONFIGURATION="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 3 ;;
  esac
done

if [[ ! -f "$SNAPSHOT" ]]; then echo "Snapshot not found: $SNAPSHOT" >&2; exit 3; fi
if [[ ! -f "$GOLDEN" ]]; then echo "Golden file not found: $GOLDEN" >&2; exit 3; fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$REPO_ROOT/Cli/MemorySnapshotDataTools.Cli.csproj"

# Default database path: a temp file matching the chosen backend.
EXT="duckdb"
if [[ "$DESTINATION" == "sqlite" ]]; then EXT="db"; fi
CLEANUP_DB=0
if [[ -z "$DB_PATH" ]]; then
  DB_PATH="$(mktemp -t msdt_validate_XXXXXX).$EXT"
  rm -f "$DB_PATH"
  if [[ "$KEEP_DB" -eq 0 ]]; then CLEANUP_DB=1; fi
fi

cleanup() {
  if [[ "$CLEANUP_DB" -eq 1 ]]; then
    rm -f "$DB_PATH" "$DB_PATH.wal"
  fi
}
trap cleanup EXIT

echo "==> Exporting $SNAPSHOT -> $DB_PATH ($DESTINATION)"
dotnet run --project "$CLI_PROJECT" -c "$CONFIGURATION" -- \
  export "$SNAPSHOT" "$DB_PATH" --destination "$DESTINATION"

echo "==> Validating against $GOLDEN"
VALIDATE_ARGS=(validate "$GOLDEN" "$DB_PATH")
if [[ -n "$RESULT_PATH" ]]; then
  VALIDATE_ARGS+=(--out "$RESULT_PATH")
fi

set +e
dotnet run --project "$CLI_PROJECT" -c "$CONFIGURATION" -- "${VALIDATE_ARGS[@]}"
STATUS=$?
set -e

exit "$STATUS"
