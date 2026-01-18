#!/bin/bash
# Publish for production
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

OUTPUT_DIR="${1:-$PROJECT_ROOT/publish}"

echo "Publishing to $OUTPUT_DIR..."

dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj \
    -c Release \
    -o "$OUTPUT_DIR"

echo ""
echo "Published to: $OUTPUT_DIR"
