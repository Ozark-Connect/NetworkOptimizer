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
#   --lan-speed-test Host the LAN speed test page (port 24443) and iperf3 (5201)
#   --insecure       Accept a self-signed cert on the server's reverse proxy
#   --dir PATH       Install directory (default: /opt/network-optimizer-agent)
#   --uninstall      Stop and remove the agent container and install dir, then exit

set -euo pipefail

SERVER=""
TOKEN=""
LAN_SPEED_TEST=false
INSECURE=false
UNINSTALL=false
INSTALL_DIR="/opt/network-optimizer-agent"
CONTAINER_NAME="network-optimizer-agent"
COMPOSE_URL="https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/agent/docker-compose.yml"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --lan-speed-test) LAN_SPEED_TEST=true; shift ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# --- Output helpers -----------------------------------------------------------
# Colorized, structured output; colors collapse to empty when stdout isn't a
# terminal, so piped/logged output stays clean.
if [ -t 1 ]; then
    _b=$'\e[1m'; _dim=$'\e[2m'; _grn=$'\e[32m'; _ylw=$'\e[33m'; _red=$'\e[31m'; _cyn=$'\e[36m'; _rst=$'\e[0m'
else
    _b=; _dim=; _grn=; _ylw=; _red=; _cyn=; _rst=
fi
_rule="$(printf '\xe2\x94\x80%.0s' {1..52})"   # ── divider between sections
step() { printf '\n%s%s%s\n%s==>%s %s%s%s\n' "$_dim" "$_rule" "$_rst" "${_cyn}${_b}" "$_rst" "$_b" "$*" "$_rst"; }
ok()   { printf '  %s\xe2\x9c\x93%s %s\n' "$_grn" "$_rst" "$*"; }
note() { printf '  %s%s%s\n' "$_dim" "$*" "$_rst"; }
warn() { printf '  %s\xe2\x9a\xa0%s  %s\n' "$_ylw" "$_rst" "$*"; }
err()  { printf '%sError:%s %s\n' "${_red}${_b}" "$_rst" "$*" >&2; exit 1; }

# --uninstall needs neither --server nor a token; skip the install-only checks.
if [ "$UNINSTALL" != true ]; then
    [ -n "$SERVER" ] || err "--server is required (the central server's HTTPS address)"
    case "$SERVER" in
        https://*) ;;
        *) err "--server must be an https:// URL (the agent refuses cleartext)" ;;
    esac
fi

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

# True if the agent container exists in any state (running, exited, created).
container_exists() {
    $SUDO docker ps -a --format '{{.Names}}' 2>/dev/null | grep -qx "$CONTAINER_NAME"
}

# --- Teardown --------------------------------------------------------------
# 'compose down' stops and removes the container and its network - a clean,
# idempotent teardown with none of the orphaned-process risk of a systemd unit.
# A bind mount holds the data, so nothing lingers once the install dir is gone.
if [ "$UNINSTALL" = true ]; then
    step "Removing the Network Optimizer agent (Docker)"
    note "${CONTAINER_NAME} + ${INSTALL_DIR}"
    if [ -f "${INSTALL_DIR}/docker-compose.yml" ]; then
        $SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" down 2>/dev/null || true
    fi
    # Fallback: the compose file may already be gone while the container lingers.
    if container_exists; then
        $SUDO docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
    fi
    $SUDO rm -rf "$INSTALL_DIR"
    # Never report success while the container still exists - a survivor keeps the
    # tunnel up and relays speed-test results to the central server.
    if container_exists; then
        err "Container ${CONTAINER_NAME} is STILL present after teardown. Remove it manually: ${SUDO} docker rm -f ${CONTAINER_NAME}"
    fi
    ok "Agent removed."
    printf '\n'
    exit 0
fi

printf '\n%sNetwork Optimizer on-site agent (Docker)%s\n' "$_b" "$_rst"
note "Installing to ${INSTALL_DIR}"
$SUDO mkdir -p "${INSTALL_DIR}/data"

step "Fetching the compose template"
$SUDO curl -fsSL "$COMPOSE_URL" -o "${INSTALL_DIR}/docker-compose.yml"
ok "docker-compose.yml"

CONFIG="${INSTALL_DIR}/data/agent.json"

step "Configuring the agent"
# Preserve an already-enrolled config so re-running the installer (e.g. to
# update the image) never wipes the persisted agent key - unless this run
# brought a new token, which means re-enroll: the agent only enrolls when it
# has no key, so the old key and site go and the token takes their place.
FRESH_CONFIG=false
if $SUDO grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    if [ -n "$TOKEN" ]; then
        FRESH_CONFIG=true
        note "Re-enrolling with the new token - the previous agent key is discarded"
        $SUDO cp -p "$CONFIG" "${CONFIG}.bak"
        note "Previous config saved to ${CONFIG}.bak"
        $SUDO sed -i -e '/^[[:space:]]*"agentKey":/d' -e '/^[[:space:]]*"siteSlug":/d' "$CONFIG"
        if $SUDO grep -q '"enrollmentToken"' "$CONFIG"; then
            $SUDO sed -i "s|\"enrollmentToken\": *[^,]*|\"enrollmentToken\": \"${TOKEN}\"|" "$CONFIG"
        else
            $SUDO sed -i "0,/{/s/{/{\n  \"enrollmentToken\": \"${TOKEN}\",/" "$CONFIG"
        fi
        # A deleted last property leaves a trailing comma behind.
        $SUDO sed -i -z 's/,\([[:space:]]*\)}/\1}/' "$CONFIG"
        ok "Updated ${CONFIG}"
    else
        note "Existing enrollment found - keeping agent.json"
    fi
else
    FRESH_CONFIG=true
    [ -n "$TOKEN" ] || err "--token is required for a first-time install"
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
    ok "Wrote ${CONFIG}"
fi

step "Starting the agent"
$SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" pull
# The agent reads agent.json once at startup. A container already running from an earlier
# enrollment keeps its old key in memory, so a freshly written config must recreate it or the
# new token is never used.
if [ "$FRESH_CONFIG" = true ]; then
    $SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" up -d --force-recreate
else
    $SUDO $COMPOSE -f "${INSTALL_DIR}/docker-compose.yml" up -d
fi
ok "container up"

step "Done"
ok "Agent started"
note "It enrolls, then holds a tunnel to ${SERVER%/} - watch it come Online in the web UI."
note "Logs:   ${SUDO} docker logs -f network-optimizer-agent"
DIR_ARG=""
[ "$INSTALL_DIR" != "/opt/network-optimizer-agent" ] && DIR_ARG=" --dir \"${INSTALL_DIR}\""
note "Remove: curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-docker.sh | ${SUDO} bash -s -- --uninstall${DIR_ARG}"
printf '\n'
