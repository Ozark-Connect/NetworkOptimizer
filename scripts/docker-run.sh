#!/bin/bash
# Run Docker container locally (macOS compatible)
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT/docker"

echo "Starting container..."
echo "Access at http://localhost:8042"
echo ""

docker compose -f docker-compose.macos.yml up -d

echo ""
echo "Container started. View logs with: docker logs -f network-optimizer"
