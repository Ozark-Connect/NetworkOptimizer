#!/bin/bash
# Run all tests
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT"

echo "Running all tests..."
dotnet test --no-restore

echo ""
echo "All tests passed!"
