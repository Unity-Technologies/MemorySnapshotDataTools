#!/usr/bin/env bash
# Build and zip MemorySnapshotDataTools for each RID. Run from MemorySnapshotDataTools (project root).
# Produces: artifacts/MemorySnapshotDataTools-<Version>-<RID>.zip

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/Cli/MemorySnapshotDataTools.Cli.csproj"
PUBLISH_DIR="$ROOT/publish"
ARTIFACTS_DIR="$ROOT/artifacts"
RIDS=(win-x64 linux-x64 osx-x64 osx-arm64)

# Read version from csproj (e.g. <Version>0.1.0</Version>)
VERSION=$(grep -oE '<Version>[^<]+</Version>' "$PROJECT" | sed 's/<[^>]*>//g')
if [[ -z "$VERSION" ]]; then
  echo "Could not read Version from $PROJECT"
  exit 1
fi

cd "$ROOT"
mkdir -p "$PUBLISH_DIR" "$ARTIFACTS_DIR"

for RID in "${RIDS[@]}"; do
  echo "Publishing $RID..."
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$PUBLISH_DIR/$RID"
  echo "Zipping MemorySnapshotDataTools-$VERSION-$RID.zip"
  (cd "$PUBLISH_DIR/$RID" && zip -rq "$ARTIFACTS_DIR/MemorySnapshotDataTools-$VERSION-$RID.zip" .)
  rm -rf "$PUBLISH_DIR/$RID"
done

rmdir "$PUBLISH_DIR" 2>/dev/null || true
echo "Done. Artifacts in $ARTIFACTS_DIR:"
ls -la "$ARTIFACTS_DIR"/*.zip
