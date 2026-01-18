#!/bin/bash
# Build the project
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

CONFIG="${1:-Debug}"

echo "Building ($CONFIG)..."
dotnet build -c "$CONFIG"

echo ""
echo "Build complete!"
