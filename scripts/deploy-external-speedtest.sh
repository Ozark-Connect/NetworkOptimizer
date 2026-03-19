#!/bin/bash
# Deploy an external OpenSpeedTest server for WAN speed testing
# This fetches only the speedtest files needed - not the full repo
#
# Usage:
#   ./deploy-external-speedtest.sh <optimizer-url> <server-name> [port]
#
# Example:
#   ./deploy-external-speedtest.sh https://optimizer.example.com vps-east 3005
#
# Prerequisites: Docker and Docker Compose on the target machine

set -e

OPTIMIZER_URL="${1:?Usage: $0 <optimizer-url> <server-name> [port]}"
SERVER_NAME="${2:?Usage: $0 <optimizer-url> <server-name> [port]}"
PORT="${3:-3005}"
INSTALL_DIR="/opt/netopt-speed-test"
BRANCH="${BRANCH:-main}"
GITHUB_REPO="Ozark-Connect/NetworkOptimizer"

echo "=== Network Optimizer - External Speed Test Server ==="
echo "Optimizer URL: $OPTIMIZER_URL"
echo "Server Name:   $SERVER_NAME"
echo "Port:          $PORT"
echo "Install Dir:   $INSTALL_DIR"
echo ""

# Check Docker
if ! command -v docker &> /dev/null; then
    echo "Error: Docker is not installed"
    exit 1
fi

if ! docker compose version &> /dev/null; then
    echo "Error: Docker Compose is not installed"
    exit 1
fi

# Create install directory
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"

# Download required files from GitHub
BASE_URL="https://raw.githubusercontent.com/$GITHUB_REPO/$BRANCH"

echo "Downloading speed test files..."
mkdir -p src/OpenSpeedTest/assets/js src/OpenSpeedTest/assets/css src/OpenSpeedTest/assets/images
mkdir -p docker/openspeedtest

# Core files
curl -sL "$BASE_URL/docker/openspeedtest/Dockerfile" -o docker/openspeedtest/Dockerfile
curl -sL "$BASE_URL/docker/openspeedtest/nginx.conf" -o docker/openspeedtest/nginx.conf
curl -sL "$BASE_URL/docker/openspeedtest/entrypoint.sh" -o docker/openspeedtest/entrypoint.sh

# OpenSpeedTest UI files (download all from the assets)
for f in index.html; do
    curl -sL "$BASE_URL/src/OpenSpeedTest/$f" -o "src/OpenSpeedTest/$f"
done

for f in config.js app-2.5.4.js app-2.5.4.min.js geolocation.js darkmode.js; do
    curl -sL "$BASE_URL/src/OpenSpeedTest/assets/js/$f" -o "src/OpenSpeedTest/assets/js/$f"
done

for f in ozark-overrides.css; do
    curl -sL "$BASE_URL/src/OpenSpeedTest/assets/css/$f" -o "src/OpenSpeedTest/assets/css/$f"
done

# Download all images (favicon, logo, etc.)
for f in apple-touch-icon.png favicon.ico favicon.png logo-dark.svg logo.svg; do
    curl -sL "$BASE_URL/src/OpenSpeedTest/assets/images/$f" -o "src/OpenSpeedTest/assets/images/$f" 2>/dev/null || true
done

# Create .dockerignore
cat > .dockerignore << 'EOF'
.git
*.md
tests/
scripts/
research/
plans/
EOF

# Create docker-compose.yml
cat > docker-compose.yml << COMPOSE_EOF
services:
  speedtest:
    build:
      context: .
      dockerfile: docker/openspeedtest/Dockerfile
    container_name: netopt-wan-speedtest
    restart: unless-stopped
    ports:
      - "${PORT}:3000"
    environment:
      - TZ=\${TZ:-UTC}
      - REVERSE_PROXIED_HOST_NAME=$(echo "$OPTIMIZER_URL" | sed 's|https\?://||' | sed 's|/.*||')
      - EXTERNAL_SERVER_ID=${SERVER_NAME}
COMPOSE_EOF

echo "Building and starting speed test server..."
docker compose build
docker compose up -d

echo ""
echo "=== Deployment Complete ==="
echo "Speed test URL: http://$(hostname -I | awk '{print $1}'):$PORT"
echo ""
echo "IMPORTANT: HTTPS is required for results to post back to Network Optimizer."
echo "Browsers block requests from public HTTP pages to private network addresses."
echo "The reverse proxy must also force HTTP/1.1 (HTTP/2 interferes with speed test accuracy)."
echo ""
echo "Recommended: Traefik or Caddy with HTTP/1.1 and TLS."
echo "See DEPLOYMENT.md for setup instructions."
echo ""
echo "Then configure Network Optimizer Settings -> External Speed Test Server:"
echo "  - Host: speedtest.yourdomain.com"
echo "  - Port: 443"
echo "  - Scheme: HTTPS"
echo "  - Name: $SERVER_NAME"
