#!/bin/bash
# Deploy or update an external OpenSpeedTest server for WAN speed testing
# This fetches only the speedtest files needed - not the full repo
#
# Usage:
#   Fresh install (interactive):
#     ./deploy-external-speedtest.sh
#
#   Fresh install (from Settings-generated command):
#     ./deploy-external-speedtest.sh <optimizer-url> <server-id> [port]
#
#   Update existing installation:
#     ./deploy-external-speedtest.sh --update
#
# Prerequisites: Docker and Docker Compose on the target machine

set -e

INSTALL_DIR="/opt/netopt-speed-test"
BRANCH="${BRANCH:-main}"
GITHUB_REPO="Ozark-Connect/NetworkOptimizer"

# --- Slug generation (must match C# ExternalSpeedTestServer.GenerateServerId) ---
generate_server_id() {
    echo "$1" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g' | sed 's/--*/-/g' | sed 's/^-//;s/-$//'
}

# --- Download all required files from GitHub via tarball ---
# Uses the GitHub API to download a tarball of the repo, then extracts only
# the files needed for the speed test container. No file list to maintain.
download_files() {
    local TARBALL_URL="https://github.com/$GITHUB_REPO/archive/refs/heads/$BRANCH.tar.gz"
    local TEMP_TAR=$(mktemp)
    local TEMP_DIR=$(mktemp -d)

    curl -sL "$TARBALL_URL" -o "$TEMP_TAR"

    # Extract only the directories we need
    # Tarball root is NetworkOptimizer-<branch>/
    local STRIP=1  # strip the root directory
    # Extract only the directories we need
    # --wildcards is needed on GNU tar (Linux), but errors on BSD tar (macOS)
    if tar --version 2>&1 | grep -q GNU; then
        tar -xzf "$TEMP_TAR" -C "$TEMP_DIR" --strip-components=$STRIP --wildcards \
            "*/src/OpenSpeedTest/" \
            "*/docker/openspeedtest/"
    else
        tar -xzf "$TEMP_TAR" -C "$TEMP_DIR" --strip-components=$STRIP \
            "*/src/OpenSpeedTest/" \
            "*/docker/openspeedtest/"
    fi

    # Copy into install directory
    mkdir -p docker/openspeedtest src/OpenSpeedTest
    cp -r "$TEMP_DIR/docker/openspeedtest/"* docker/openspeedtest/
    cp -r "$TEMP_DIR/src/OpenSpeedTest/"* src/OpenSpeedTest/

    rm -rf "$TEMP_TAR" "$TEMP_DIR"
}

# --- Update mode ---
if [ "${1}" = "--update" ]; then
    if [ ! -f "$INSTALL_DIR/docker-compose.yml" ]; then
        echo "Error: No existing installation found at $INSTALL_DIR"
        echo "Run without --update to do a fresh install."
        exit 1
    fi

    cd "$INSTALL_DIR"
    BASE_URL="https://raw.githubusercontent.com/$GITHUB_REPO/$BRANCH"

    echo "=== Updating External Speed Test Server ==="
    echo ""
    echo "Downloading latest files..."

    download_files

    echo "Rebuilding container..."
    docker compose build
    docker compose up -d

    echo ""
    echo "=== Update Complete ==="
    exit 0
fi

# --- Fresh install ---

# Check Docker first
if ! command -v docker &> /dev/null; then
    echo "Error: Docker is not installed"
    exit 1
fi

if ! docker compose version &> /dev/null; then
    echo "Error: Docker Compose is not installed"
    exit 1
fi

# If args provided, use them (non-interactive / Settings-generated command)
if [ -n "$1" ]; then
    OPTIMIZER_URL="$1"
    SERVER_ID="${2:-external}"
    PORT="${3:-3005}"
else
    # Interactive mode
    echo "=== Network Optimizer - External Speed Test Server Setup ==="
    echo ""
    echo "This sets up a remote speed test server that your network clients can use"
    echo "to measure their real internet (WAN) speed. Results are posted back to your"
    echo "Network Optimizer instance automatically."
    echo ""

    # Optimizer URL
    echo "What is the URL of your Network Optimizer instance?"
    echo "  This is the HTTPS address your browser uses to access Network Optimizer."
    echo "  It must be HTTPS - browsers block speed test results from being posted"
    echo "  back to a private network address unless the page is served over HTTPS."
    echo "  Examples: https://optimizer.example.com, https://192.168.1.100:8042"
    echo ""
    read -rp "Optimizer URL: " OPTIMIZER_URL < /dev/tty
    if [ -z "$OPTIMIZER_URL" ]; then
        echo "Error: Optimizer URL is required."
        exit 1
    fi

    echo ""

    # Server ID
    echo "If you've already configured this server in Network Optimizer Settings,"
    echo "enter the Server ID shown there. Otherwise, enter a friendly name and"
    echo "we'll generate the ID for you."
    echo ""
    read -rp "Server ID or name (e.g. vps-chicago or VPS Chicago): " SERVER_INPUT < /dev/tty
    if [ -z "$SERVER_INPUT" ]; then
        echo "Error: Server ID or name is required."
        exit 1
    fi

    # Check if it looks like a slug already (lowercase, hyphens, numbers only) or a display name
    if echo "$SERVER_INPUT" | grep -qE '^[a-z0-9][a-z0-9-]*[a-z0-9]$'; then
        SERVER_ID="$SERVER_INPUT"
    else
        SERVER_ID=$(generate_server_id "$SERVER_INPUT")
        echo ""
        echo "  Generated Server ID: $SERVER_ID"
        echo ""
        echo "  Important: When you configure this server in Network Optimizer Settings,"
        echo "  use the name \"$SERVER_INPUT\" so the Server IDs match."
    fi

    echo ""

    # Port
    read -rp "Port [3005]: " PORT < /dev/tty
    PORT="${PORT:-3005}"

    echo ""
fi

echo "=== Network Optimizer - External Speed Test Server ==="
echo "Optimizer URL: $OPTIMIZER_URL"
echo "Server ID:     $SERVER_ID"
echo "Port:          $PORT"
echo "Install Dir:   $INSTALL_DIR"
echo ""

# Create install directory
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"

echo "Downloading speed test files..."
download_files

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
      - EXTERNAL_SERVER_ID=${SERVER_ID}
COMPOSE_EOF

echo "Building and starting speed test server..."
docker compose build
docker compose up -d

echo ""
echo "=== Deployment Complete ==="
echo "Speed test URL: http://$(hostname -I 2>/dev/null | awk '{print $1}' || echo 'localhost'):$PORT"
echo ""
echo "IMPORTANT: HTTPS is strongly recommended. Most browsers (Chrome, Edge, Safari)"
echo "block results from posting back when the speed test page is served over HTTP."
echo "Set up a reverse proxy (Traefik or Caddy) with TLS and HTTP/1.1."
echo "See DEPLOYMENT.md for setup instructions."
echo ""
echo "Then configure Network Optimizer Settings -> External Speed Test Server:"
echo "  - Name: (use the same name you entered here so the Server IDs match)"
echo "  - Host: speedtest.yourdomain.com"
echo "  - Port: 443"
echo "  - Scheme: HTTPS"
echo ""
echo "To update in the future, run:"
echo "  curl -fsSL https://raw.githubusercontent.com/$GITHUB_REPO/main/scripts/deploy-external-speedtest.sh | bash -s -- --update"
