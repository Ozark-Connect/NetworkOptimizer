#!/usr/bin/env bash
#
# Network Optimizer on-site agent - bare-metal (systemd) installer.
#
# Downloads the self-contained agent binary (no .NET runtime or Docker needed),
# writes the agent config, installs a systemd service, and starts it. Generate
# the enrollment token in the central server's web UI under Settings >
# Multi-Site > (site) > Agents > Set up agent.
#
#   curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-native.sh | sudo bash -s -- \
#     --server "https://optimizer.example.com" \
#     --token  "noa_..."
#
# Options:
#   --server URL     Central server HTTPS address (required; same URL as the app)
#   --token  TOKEN   One-time enrollment token (required on first install)
#   --lan-speed-test Host the LAN speed test page (port 3000) and iperf3 (5201)
#   --insecure       Accept a self-signed cert on the server's reverse proxy
#   --dir PATH       Install directory (default: /opt/netopt-agent)

set -euo pipefail

SERVER=""
TOKEN=""
LAN_SPEED_TEST=false
INSECURE=false
INSTALL_DIR="/opt/netopt-agent"
SERVICE_NAME="netopt-agent"
RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/latest/download"

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

[ "$(id -u)" -eq 0 ] || err "Run as root (needed to install the systemd service): sudo bash install-native.sh ..."
[ -n "$SERVER" ] || err "--server is required (the central server's HTTPS address)"
case "$SERVER" in
    https://*) ;;
    *) err "--server must be an https:// URL (the agent refuses cleartext)" ;;
esac
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)"
command -v curl >/dev/null 2>&1 || err "curl is required"

# Map machine architecture to the published self-contained runtime identifier.
case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) err "Unsupported architecture: $(uname -m). Build from source (see the agent README)." ;;
esac

echo "Installing Network Optimizer agent to ${INSTALL_DIR} (${RID})"
mkdir -p "$INSTALL_DIR"

# Agent binary
echo "Downloading agent binary..."
curl -fSL "${RELEASE_BASE}/NetworkOptimizer.Agent-${RID}" -o "${INSTALL_DIR}/NetworkOptimizer.Agent"
chmod +x "${INSTALL_DIR}/NetworkOptimizer.Agent"

# uwnspeedtest binary for site-local WAN speed tests; the agent resolves it next
# to itself (AppContext.BaseDirectory/uwnspeedtest).
echo "Downloading WAN speed test binary..."
curl -fSL "${RELEASE_BASE}/uwnspeedtest-${RID}" -o "${INSTALL_DIR}/uwnspeedtest"
chmod +x "${INSTALL_DIR}/uwnspeedtest"

CONFIG="${INSTALL_DIR}/agent.json"

# Preserve an already-enrolled config so re-running the installer (e.g. to
# update the binary) never wipes the persisted agent key.
if grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    echo "Existing enrolled agent config found - keeping it."
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install"
    echo "Writing ${CONFIG}"
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
    } > "$CONFIG"
fi

# nginx serves the OpenSpeedTest page + the throughput-critical transfer legs
# (sendfile, 10 GbE); the agent's loopback relay (127.0.0.1:3001) forwards result
# posts to the central server. Only needed with --lan-speed-test.
if [ "$LAN_SPEED_TEST" = true ]; then
    echo "Setting up nginx for the LAN speed test..."
    if ! command -v nginx >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq nginx
        elif command -v dnf >/dev/null 2>&1; then dnf install -y -q nginx
        elif command -v yum >/dev/null 2>&1; then yum install -y -q nginx
        elif command -v apk >/dev/null 2>&1; then apk add --no-cache nginx
        else echo "WARNING: could not install nginx automatically - install it and re-run to enable the LAN speed test."; fi
    fi

    if command -v nginx >/dev/null 2>&1; then
        WEBROOT="/usr/share/nginx/html"
        RAW="https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main"
        mkdir -p "$WEBROOT/assets/js"
        echo "Downloading OpenSpeedTest..."
        TARBALL="$(mktemp)"; TMPX="$(mktemp -d)"
        curl -fsSL "https://github.com/Ozark-Connect/NetworkOptimizer/archive/refs/heads/main.tar.gz" -o "$TARBALL"
        tar -xzf "$TARBALL" -C "$TMPX" --strip-components=3 "NetworkOptimizer-main/src/OpenSpeedTest"
        cp -r "$TMPX/." "$WEBROOT/"
        rm -rf "$TARBALL" "$TMPX"

        # Results relay same-origin through the agent (overrides the placeholder config.js).
        cat > "$WEBROOT/assets/js/config.js" <<'CFGJS'
var saveData = true;
var saveDataURL = window.location.protocol + "//" + window.location.host + "/api/public/speedtest/results";
var apiPath = "/api/public/speedtest/results";
var externalServerId = "";
var clientResultsUrl = window.location.protocol + "//" + window.location.host + "/client-speedtest";
var OpenSpeedTestdb = "";
CFGJS

        mkdir -p /etc/nginx/conf.d
        curl -fsSL "$RAW/docker/agent/nginx.conf" -o /etc/nginx/conf.d/netopt-agent-speedtest.conf
        rm -f /etc/nginx/sites-enabled/default 2>/dev/null || true
        if nginx -t >/dev/null 2>&1; then
            systemctl enable nginx >/dev/null 2>&1 || true
            systemctl restart nginx 2>/dev/null || service nginx restart 2>/dev/null || true
            echo "nginx serving OpenSpeedTest on port 3000."
        else
            echo "WARNING: nginx config test failed - the LAN speed test page won't serve. Diagnose with: nginx -t"
        fi
    fi
fi

# systemd unit
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
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}.service"

echo
echo "Agent started. It enrolls, then holds a tunnel to ${SERVER%/}."
echo "Watch it come Online in the web UI, or follow logs:"
echo "  journalctl -u ${SERVICE_NAME} -f"
