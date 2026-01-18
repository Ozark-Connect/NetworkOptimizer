#!/bin/bash
# Build Docker image locally
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_ROOT/docker"

IMAGE_NAME="${1:-network-optimizer}"
TAG="${2:-latest}"

echo "Building Docker image: $IMAGE_NAME:$TAG"

docker compose build network-optimizer

echo ""
echo "Image built: $IMAGE_NAME:$TAG"
