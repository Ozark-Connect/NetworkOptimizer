#!/bin/bash
# Publish for production
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

OUTPUT_DIR="${1:-$PROJECT_ROOT/publish}"
VERSION="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || true)}"
VERSION="${VERSION:-0.0.0}"

echo "Publishing to $OUTPUT_DIR..."

# A direct .NET publish is also used for native Linux deployments. Build the complete AP
# payload first so the Web project can copy it into tools/.
if ! command -v go >/dev/null 2>&1; then
    echo "Go is required to build the AP Agent payload." >&2
    exit 1
fi

make -C src/apagent build-ap VERSION="$VERSION"

dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj \
    -c Release \
    -o "$OUTPUT_DIR"

echo ""
echo "Published to: $OUTPUT_DIR"
