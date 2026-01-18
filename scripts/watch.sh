#!/bin/bash
# Run the web app with hot reload
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT/src/NetworkOptimizer.Web"

echo "Starting with hot reload..."
echo "Access at http://localhost:5000"
echo ""

dotnet watch run
