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
#     fence so the agent stays well clear of routing/IPS
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
#   --uninstall    Stop + remove the service and install dir, then exit
#
# The install directory is fixed at /data/netopt-agent and is intentionally NOT
# configurable: on UniFi OS only /data survives a firmware upgrade (it is the
# writable upper layer of the root overlay), so relocating the agent elsewhere
# would silently make it disappear on the next upgrade.
#
# Re-running the installer upgrades the agent in place: it downloads the latest
# release, keeps the enrolled key, and restarts the service on the new binary.
#
# Survives firmware upgrades with no action needed: on UniFi OS the root
# filesystem is an overlay whose writable upper layer IS the persistent /data
# partition, so a unit written to /etc/systemd/system physically lands on
# persistent storage (this is exactly how udm-boot itself survives). The binary,
# config, and systemd unit all carry across a firmware upgrade untouched. (A
# factory reset wipes /data and needs a fresh install, like anything else.)

set -euo pipefail

SERVER=""
TOKEN=""
# Fixed, not configurable: /data is the only path that survives a UniFi OS firmware
# upgrade (writable upper layer of the root overlay). See the header note.
INSTALL_DIR="/data/netopt-agent"
SERVICE_NAME="netopt-agent"
INSECURE=false
UNINSTALL=false
RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/latest/download"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
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

# PIDs of any agent still running from this install dir. Matches the binary path in
# the process argv (which /proc keeps even after the binary file is deleted), so it
# still finds a process an earlier broken uninstall reparented to init. UniFi OS
# ships pgrep, but fall back to ps -ef for robustness.
agent_pids() {
    if command -v pgrep >/dev/null 2>&1; then
        pgrep -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
    else
        ps -ef 2>/dev/null | grep "${INSTALL_DIR}/NetworkOptimizer.Agent" | grep -v grep | awk '{print $2}' || true
    fi
}

agent_running() { [ -n "$(agent_pids)" ]; }

[ "$(id -u)" -eq 0 ] || err "Run as root (the gateway's default SSH user is root)."
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)."

# --- Teardown --------------------------------------------------------------
if [ "$UNINSTALL" = true ]; then
    step "Removing the Network Optimizer agent"
    note "${SERVICE_NAME} + ${INSTALL_DIR}"
    # 'disable --now' alone is NOT enough: a prior partial uninstall can leave the
    # unit 'Loaded: not-found' but still 'active (running)', and disable then aborts
    # on the missing unit file and skips the '--now' stop, leaving the cgroup alive.
    # Stop/kill the loaded runtime unit directly (its cgroup is intact), then reap
    # any process orphaned to init, then verify nothing survived.
    systemctl stop "${SERVICE_NAME}.service" 2>/dev/null || true
    systemctl kill --signal=SIGKILL "${SERVICE_NAME}.service" 2>/dev/null || true
    systemctl disable "${SERVICE_NAME}.service" 2>/dev/null || true
    if agent_running; then
        warn "Agent still running after systemctl stop; reaping the process directly"
        if command -v pkill >/dev/null 2>&1; then
            pkill -TERM -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
            for _ in 1 2 3 4 5; do agent_running || break; sleep 1; done
            pkill -KILL -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
        else
            pids="$(agent_pids)"; [ -n "$pids" ] && kill -TERM $pids 2>/dev/null || true
            for _ in 1 2 3 4 5; do agent_running || break; sleep 1; done
            pids="$(agent_pids)"; [ -n "$pids" ] && kill -KILL $pids 2>/dev/null || true
        fi
    fi
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload 2>/dev/null || true
    systemctl reset-failed "${SERVICE_NAME}.service" 2>/dev/null || true
    rm -rf "$INSTALL_DIR"
    if agent_running; then
        err "Agent process is STILL running after teardown (PID(s): $(agent_pids | tr '\n' ' ')). Kill it manually (e.g. kill -9 <pid>) and re-run --uninstall."
    fi
    ok "Removed - the gateway is back to stock."
    printf '\n'
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
# Skipped when the service is already running (an update re-run - its memory
# is already accounted for in MemAvailable).
MIN_AVAILABLE_MB=256
if ! systemctl is-active --quiet "${SERVICE_NAME}.service"; then
    step "Memory pre-flight"
    AVAILABLE_MB="$(awk '/MemAvailable/ {print int($2/1024)}' /proc/meminfo)"
    if [ -z "$AVAILABLE_MB" ]; then
        warn "could not read MemAvailable from /proc/meminfo - skipping the memory check."
    elif [ "$AVAILABLE_MB" -lt "$MIN_AVAILABLE_MB" ]; then
        err "only ${AVAILABLE_MB} MB of memory is available; the agent needs ${MIN_AVAILABLE_MB} MB of headroom so it stays well clear of routing/IPS. Free up memory (e.g. remove unused UniFi applications) or run the agent on a separate box (see install-native.sh)."
    else
        ok "${AVAILABLE_MB} MB available (need ${MIN_AVAILABLE_MB} MB)"
    fi
fi

printf '\n%sNetwork Optimizer on-site agent (gateway, monitoring-only)%s\n' "$_b" "$_rst"
note "Installing to ${INSTALL_DIR}  (${RID})"
mkdir -p "$INSTALL_DIR"

# Download to a temp name and rename into place: writing over the binary while
# the agent is running fails with ETXTBSY, but rename swaps the directory entry
# and the running process keeps its old inode until the restart below.
step "Downloading agent binary"
curl -fsSL "${RELEASE_BASE}/NetworkOptimizer.Agent-${RID}" -o "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
chmod +x "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
mv -f "${INSTALL_DIR}/NetworkOptimizer.Agent.new" "${INSTALL_DIR}/NetworkOptimizer.Agent"
ok "agent (${RID})"

CONFIG="${INSTALL_DIR}/agent.json"

step "Configuring the agent"
# Preserve an already-enrolled config so re-running to update the binary never
# wipes the persisted key. A token on top of an enrolled config re-enrolls ONLY when
# the server no longer accepts the existing key (agent removed or disabled): a saved
# install command carries its original, already-used token, and re-running it is how
# people upgrade, so discarding a working key on that would loop the agent on a dead
# token. The probe is the heartbeat every agent version already sends.
if grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    if [ -n "$TOKEN" ]; then
        CURRENT_KEY=$(sed -n 's/^[[:space:]]*"agentKey":[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG" | head -n 1)
        SERVER_URL=$(sed -n 's/^[[:space:]]*"serverUrl":[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG" | head -n 1)
        [ -n "$SERVER_URL" ] || SERVER_URL="$SERVER"
        CURL_TLS=""
        if [ "$INSECURE" = true ] || grep -q '"ignoreSslErrors":[[:space:]]*true' "$CONFIG"; then CURL_TLS="-k"; fi
        KEY_STATUS=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 $CURL_TLS \
            -H 'Content-Type: application/json' -d "{\"agentKey\":\"${CURRENT_KEY}\"}" \
            "${SERVER_URL%/}/api/public/agents/heartbeats" 2>/dev/null)
        case "$KEY_STATUS" in
            204)
                note "Existing enrollment is still valid - keeping the agent key (the token is not needed)"
                ;;
            401)
                note "The server no longer accepts this agent's key - re-enrolling with the token"
                cp -p "$CONFIG" "${CONFIG}.bak"
                note "Previous config saved to ${CONFIG}.bak"
                sed -i -e '/^[[:space:]]*"agentKey":/d' -e '/^[[:space:]]*"siteSlug":/d' "$CONFIG"
                if grep -q '"enrollmentToken"' "$CONFIG"; then
                    sed -i "s|\"enrollmentToken\": *[^,]*|\"enrollmentToken\": \"${TOKEN}\"|" "$CONFIG"
                else
                    sed -i "0,/{/s/{/{\n  \"enrollmentToken\": \"${TOKEN}\",/" "$CONFIG"
                fi
                # A deleted last property leaves a trailing comma behind.
                sed -i -z 's/,\([[:space:]]*\)}/\1}/' "$CONFIG"
                ok "Updated ${CONFIG}"
                ;;
            *)
                warn "Could not verify the existing key with ${SERVER_URL} (HTTP ${KEY_STATUS}) - keeping agent.json unchanged. Re-run once the server is reachable, or remove the agent in the app to force a fresh enrollment."
                ;;
        esac
    else
        note "Existing enrollment found - keeping agent.json"
    fi
    # Upgrades keep the enrolled config by design, so the on-gateway key has to be ADDED to
    # an existing agent.json rather than assumed present (#1108) - without this, every
    # gateway agent installed before the flag existed would stay on the server's
    # IP-correlation fallback forever. This installer only ever runs on a gateway, so the
    # answer is unconditionally true.
    if ! grep -q '"onGateway"' "$CONFIG"; then
        sed -i '0,/{/s/{/{\n  "onGateway": true,/' "$CONFIG"
        grep -q '"onGateway"' "$CONFIG" || warn "could not add onGateway to ${CONFIG} - add \"onGateway\": true by hand"
        ok "Marked the install on-gateway in agent.json"
    fi
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install."
    {
        echo "{"
        echo "  \"serverUrl\": \"${SERVER%/}\","
        echo "  \"tunnelUrl\": \"${SERVER%/}\","
        echo "  \"enrollmentToken\": \"${TOKEN}\","
        echo "  \"onGateway\": true,"
        printf '  "ignoreSslErrors": %s\n' "$INSECURE"
        echo "}"
    } > "$CONFIG"
    chmod 600 "$CONFIG"
    ok "Wrote ${CONFIG}"
fi

step "Installing the agent service"
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
# well above its ~50 MB steady state so a fault stays well clear of routing/IPS,
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
systemctl enable --quiet "${SERVICE_NAME}.service"
# restart (not `enable --now`) so an upgrade re-run moves an already-running
# agent onto the new binary; it starts a stopped/fresh service just the same
systemctl restart "${SERVICE_NAME}.service"
ok "${SERVICE_NAME}.service installed and started"

step "Done"
ok "Agent installed and running (monitoring-only)"
note "It enrolls, then holds a tunnel to ${SERVER%/} - watch it come Online in the web UI."
note "Logs:   journalctl -u ${SERVICE_NAME} -f"
note "Remove: curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- --uninstall"
printf '\n'
