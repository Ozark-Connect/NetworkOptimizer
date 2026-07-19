#!/usr/bin/env bash
#
# Network Optimizer on-site agent - UniFi gateway (on-box) installer.
#
# For running the agent directly on a UniFi gateway instead of a separate site
# box. Any current UniFi OS gateway (UCG, UXG, UDM, UDR, EFG lines) works - there is
# no model gate; the memory pre-flight below is the only capability check. Monitoring only: the LAN speed test
# is intentionally NOT installed here - hosting an nginx/iperf3 speed-test server
# on the router would compete with the data plane. For LAN speed testing, run a
# Docker or bare-metal agent on a separate box (see install-native.sh).
#
# Differences from install-native.sh, all for the gateway environment:
#   - installs to /data (persistent on UniFi OS) rather than /opt
#   - a systemd unit tuned for a shared router box: workstation GC and a memory
#     fence so the agent can never pressure routing/IPS
#   - no speed-test machinery (no nginx, no iperf3, no uwnspeedtest)
#   - an --uninstall path for clean teardown
#
# UniFi gateways SSH in as root, so no sudo is needed:
#   curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- \
#     --server "https://optimizer.example.com" \
#     --token  "noa_..."
#
# Options:
#   --server URL   Central server HTTPS address (required; the same URL as the app)
#   --token  TOK   One-time enrollment token (required on first install)
#   --insecure     Accept a self-signed cert on the server's reverse proxy
#   --dir PATH     Install directory (default: /data/netopt-agent)
#   --uninstall    Stop + remove the service and install dir, then exit
#
# Re-running the installer upgrades the agent in place: it downloads the latest
# release, keeps the enrolled key, and restarts the service on the new binary.
#
# NOTE: the systemd unit lives on the overlay root, so it does not survive a
# UniFi OS firmware update. The binary and config under /data do persist. After a
# firmware update, re-run this installer (it keeps the enrolled key) to reinstate
# the service.

set -euo pipefail

SERVER=""
TOKEN=""
INSTALL_DIR="/data/netopt-agent"
SERVICE_NAME="netopt-agent"
INSECURE=false
UNINSTALL=false
RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/latest/download"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

err() { echo "Error: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || err "Run as root (the gateway's default SSH user is root)."
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)."

# --- Teardown --------------------------------------------------------------
if [ "$UNINSTALL" = true ]; then
    echo "Removing ${SERVICE_NAME} and ${INSTALL_DIR}..."
    systemctl disable --now "${SERVICE_NAME}.service" 2>/dev/null || true
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload 2>/dev/null || true
    rm -rf "$INSTALL_DIR"
    echo "Done - the gateway is back to stock."
    exit 0
fi

# --- Install ---------------------------------------------------------------
[ -n "$SERVER" ] || err "--server is required (the central server's HTTPS address)."
case "$SERVER" in
    https://*) ;;
    *) err "--server must be an https:// URL (the agent refuses cleartext)." ;;
esac
command -v curl >/dev/null 2>&1 || err "curl is required."

# Map machine architecture to the published self-contained runtime identifier.
case "$(uname -m)" in
    aarch64|arm64) RID="linux-arm64" ;;
    x86_64|amd64)  RID="linux-x64" ;;
    *) err "Unsupported architecture: $(uname -m). Build from source (see the agent README)." ;;
esac

# Memory pre-flight: the agent's real steady-state cost is ~50 MB, but the unit
# fences it at MemoryHigh=256M, so require that much headroom before installing.
# Skipped when the service is already running (an update/reinstate - its memory
# is already accounted for in MemAvailable).
MIN_AVAILABLE_MB=256
if ! systemctl is-active --quiet "${SERVICE_NAME}.service"; then
    AVAILABLE_MB="$(awk '/MemAvailable/ {print int($2/1024)}' /proc/meminfo)"
    if [ -z "$AVAILABLE_MB" ]; then
        echo "Warning: could not read MemAvailable from /proc/meminfo - skipping the memory check." >&2
    elif [ "$AVAILABLE_MB" -lt "$MIN_AVAILABLE_MB" ]; then
        err "only ${AVAILABLE_MB} MB of memory is available; the agent needs ${MIN_AVAILABLE_MB} MB of headroom so it can never pressure routing/IPS. Free up memory (e.g. remove unused UniFi applications) or run the agent on a separate box (see install-native.sh)."
    else
        echo "Memory check: ${AVAILABLE_MB} MB available (need ${MIN_AVAILABLE_MB} MB) - OK"
    fi
fi

echo "Installing Network Optimizer agent to ${INSTALL_DIR} (${RID}, monitoring-only)"
mkdir -p "$INSTALL_DIR"

# Download to a temp name and rename into place: writing over the binary while
# the agent is running fails with ETXTBSY, but rename swaps the directory entry
# and the running process keeps its old inode until the restart below.
echo "Downloading agent binary..."
curl -fSL "${RELEASE_BASE}/NetworkOptimizer.Agent-${RID}" -o "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
chmod +x "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
mv -f "${INSTALL_DIR}/NetworkOptimizer.Agent.new" "${INSTALL_DIR}/NetworkOptimizer.Agent"

CONFIG="${INSTALL_DIR}/agent.json"

# Preserve an already-enrolled config so re-running (to update the binary, or to
# reinstate the service after a firmware update) never wipes the persisted key.
if grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    echo "Existing enrolled agent config found - keeping it."
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install."
    echo "Writing ${CONFIG}"
    {
        echo "{"
        echo "  \"serverUrl\": \"${SERVER%/}\","
        echo "  \"tunnelUrl\": \"${SERVER%/}\","
        echo "  \"enrollmentToken\": \"${TOKEN}\","
        printf '  "ignoreSslErrors": %s\n' "$INSECURE"
        echo "}"
    } > "$CONFIG"
    chmod 600 "$CONFIG"
fi

echo "Installing ${SERVICE_NAME}.service"
cat > "/etc/systemd/system/${SERVICE_NAME}.service" <<UNIT
[Unit]
Description=Network Optimizer Agent (${SERVICE_NAME})
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=${INSTALL_DIR}
ExecStart=${INSTALL_DIR}/NetworkOptimizer.Agent
# Tuned for a shared router box. Workstation GC keeps the heap to a single small
# arena (server GC would allocate one per core); the memory fence caps the agent
# well above its ~50 MB steady state so a fault can never pressure routing/IPS,
# and systemd restarts it if it trips.
Environment=DOTNET_gcServer=0
MemoryHigh=256M
MemoryMax=512M
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}.service"
# restart (not `enable --now`) so an upgrade re-run moves an already-running
# agent onto the new binary; it starts a stopped/fresh service just the same
systemctl restart "${SERVICE_NAME}.service"

echo
echo "Agent started (monitoring-only). It enrolls, then holds a tunnel to ${SERVER%/}."
echo "Watch it come Online in the web UI, or follow logs:"
echo "  journalctl -u ${SERVICE_NAME} -f"
echo
echo "Remove it again with:"
echo "  bash <(curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh) --uninstall"
