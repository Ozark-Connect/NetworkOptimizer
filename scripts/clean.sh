#!/bin/bash
# Clean build artifacts and coverage
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

echo "Cleaning build artifacts..."
dotnet clean -v q

echo "Removing bin/obj directories..."
find . -type d \( -name "bin" -o -name "obj" \) -not -path "./node_modules/*" -exec rm -rf {} + 2>/dev/null || true

echo "Removing coverage directory..."
rm -rf "$PROJECT_ROOT/coverage"

echo ""
echo "Clean complete!"
