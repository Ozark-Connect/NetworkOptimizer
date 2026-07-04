#!/usr/bin/env bash
#
# Network Optimizer on-site agent - Docker installer.
#
# Pulls the agent image and compose template, writes the agent config, and
# starts it. Generate the enrollment token in the central server's web UI under
# Settings > Multi-Site > (site) > Agents > Set up agent.
#
#   curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install.sh | bash -s -- \
#     --server "https://optimizer.example.com" \
#     --token  "noa_..."
#
# Options:
#   --server URL     Central server HTTPS address (required; same URL as the app)
#   --token  TOKEN   One-time enrollment token (required on first install)
#   --lan-speed-test Host the LAN speed test page (port 3000) and iperf3 (5201)
#   --insecure       Accept a self-signed cert on the server's reverse proxy
#   --dir PATH       Install directory (default: /opt/network-optimizer-agent)

set -euo pipefail

SERVER=""
TOKEN=""
LAN_SPEED_TEST=false
INSECURE=false
INSTALL_DIR="/opt/network-optimizer-agent"
COMPOSE_URL="https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/agent/docker-compose.yml"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --lan-speed-test) LAN_SPEED_TEST=true; shift ;;
        --insecure) INSECURE=true; shift ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

err() { echo "Error: $*" >&2; exit 1; }

[ -n "$SERVER" ] || err "--server is required (the central server's HTTPS address)"
case "$SERVER" in
    https://*) ;;
    *) err "--server must be an https:// URL (the agent refuses cleartext)" ;;
esac

# Docker + compose plugin
command -v docker >/dev/null 2>&1 || err "Docker is not installed. See https://docs.docker.com/engine/install/"
if docker compose version >/dev/null 2>&1; then
    COMPOSE="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE="docker-compose"
else
    err "Docker Compose is not available (need the 'docker compose' plugin or docker-compose)"
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null 2>&1 || err "Run as root or install sudo"
    SUDO="sudo"
fi

echo "Installing Network Optimizer agent to ${INSTALL_DIR}"
$SUDO mkdir -p "${INSTALL_DIR}/data"

# Compose template
$SUDO curl -fsSL "$COMPOSE_URL" -o "${INSTALL_DIR}/docker-compose.yml"

CONFIG="${INSTALL_DIR}/data/agent.json"

# Preserve an already-enrolled config so re-running the installer (e.g. to
# update the image) never wipes the persisted agent key.
if $SUDO grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    echo "Existing enrolled agent config found - keeping it."
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install"
    echo "Writing ${CONFIG}"
    TMP_CONFIG="$(mktemp)"
    {
        echo "{"
        echo "  \"serverUrl\": \"${SERVER%/}\","
        echo "  \"tunnelUrl\": \"${SERVER%/}\","
        echo "  \"enrollmentToken\": \"${TOKEN}\","
        printf '  "ignoreSslErrors": %s' "$INSECURE"
        if [ "$LAN_SPEED_TEST" = true ]; then
            printf ',\n  "lanSpeedTest": true'
        fi
        printf '\n}\n'
    } > "$TMP_CONFIG"
    $SUDO cp "$TMP_CONFIG" "$CONFIG"
    rm -f "$TMP_CONFIG"
fi

echo "Starting agent..."
$SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" pull
$SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" up -d

echo
echo "Agent started. It enrolls, then holds a tunnel to ${SERVER%/}."
echo "Watch it come Online in the web UI, or follow logs:"
echo "  ${SUDO} docker logs -f network-optimizer-agent"
