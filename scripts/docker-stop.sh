#!/bin/bash
# Stop Docker container
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT/docker"

echo "Stopping container..."

docker compose -f docker-compose.macos.yml down

echo "Container stopped."
