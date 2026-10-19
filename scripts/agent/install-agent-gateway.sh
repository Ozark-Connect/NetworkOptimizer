#!/usr/bin/env bash
#
# Network Optimizer on-site agent - UniFi gateway (on-box) installer.
#
# For running the agent directly on a UniFi gateway instead of a separate site
# box. Any current UniFi OS gateway (UCG, UXG, UDM, UDR, EFG lines) works - there is
# no model gate; the memory pre-flight below is the only capability check.
#
# The LAN speed test is optional (--lan-speed-test). The gateway terminates the test's
# TCP itself, so a running test loads every core for its ~15 s and the ceiling is the
# gateway's own TCP stack (10 Gbps on the EFG and UDM-Beast, under about 3 Gbps on the
# UXG/UCG-Fiber class, lower on less capable gateways like the UDR7). For faster LAN
# numbers there, run a Docker or bare-metal agent on a separate box (see install-native.sh).
#
# Differences from install-native.sh, all for the gateway environment:
#   - installs to /data (persistent on UniFi OS) rather than /opt
#   - a systemd unit tuned for a shared router box: workstation GC and a memory
#     fence so the agent stays well clear of routing/IPS
#   - the speed test nginx runs as nobody with up to 4 workers inside its own memory fence,
#     and UniFi's nginx is never touched
#   - the agent's own iperf3 server replaces UniFi's iperf3.service on 5201 (stopped
#     and masked while the speed test is on, restored on removal); no uwnspeedtest
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
#   --release TAG  Install this release (e.g. v2.9.1-preview1) instead of the latest stable one
#   --lan-speed-test       Host the LAN speed test page on the gateway (port 24443).
#                          Stays on across re-runs once enabled.
#   --speed-test-port N    Serve the LAN speed test page on N instead of 24443
#   --speed-test-http-port N  Plain HTTP listener the HTTPS page hands the test to after taking
#                          the GPS fix (default 24080; 0 keeps the whole test on HTTPS)
#   --no-lan-speed-test    Remove the LAN speed test and keep the agent running
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
RELEASE_TAG=""
LAN_SPEED_TEST=false
NO_LAN_SPEED_TEST=false
SPEED_TEST_PORT=24443
PORT_REQUESTED=false
HTTP_PORT=""
SPEEDTEST_SERVICE="netopt-speedtest-nginx"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
        --release) RELEASE_TAG="$2"; shift 2 ;;
        --lan-speed-test) LAN_SPEED_TEST=true; shift ;;
        --no-lan-speed-test) NO_LAN_SPEED_TEST=true; shift ;;
        --speed-test-port) SPEED_TEST_PORT="$2"; PORT_REQUESTED=true; shift 2 ;;
        --speed-test-http-port) HTTP_PORT="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

if [ "$LAN_SPEED_TEST" = true ] && [ "$NO_LAN_SPEED_TEST" = true ]; then
    echo "--lan-speed-test and --no-lan-speed-test cannot be combined" >&2; exit 1
fi
case "$SPEED_TEST_PORT" in
    ''|*[!0-9]*) echo "Invalid --speed-test-port: $SPEED_TEST_PORT" >&2; exit 1 ;;
esac

# Same stickiness as install-native.sh: an install that serves the speed test keeps serving it
# on a plain upgrade re-run, and keeps its recorded port unless this run names another.
SERVING_SPEED_TEST=false
grep -q '"lanSpeedTest"[[:space:]]*:[[:space:]]*true' "${INSTALL_DIR}/agent.json" 2>/dev/null && SERVING_SPEED_TEST=true
if [ "$NO_LAN_SPEED_TEST" != true ] && [ "$SERVING_SPEED_TEST" = true ]; then
    LAN_SPEED_TEST=true
fi
# Only a port that was actually served is kept. Gateway agents usually gain the speed test
# after enrolling, and older configs carry a lanSpeedTestPort (3000) nobody ever served on.
if [ "$PORT_REQUESTED" != true ] && [ "$SERVING_SPEED_TEST" = true ]; then
    EXISTING_PORT=$(sed -n 's/.*"lanSpeedTestPort"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "${INSTALL_DIR}/agent.json" | head -1)
    [ -n "$EXISTING_PORT" ] && SPEED_TEST_PORT="$EXISTING_PORT"
fi

# The plain HTTP handoff port lives only in the served config.js, so a re-run reads it back there.
# An automatic off (port was taken) carries a trailing comment and is not read back: rechecked.
HTTP_PORT_NOTE=""
if [ -z "$HTTP_PORT" ]; then
    HTTP_PORT=$(sed -n 's/^var plainHttpPort = \([0-9][0-9]*\);$/\1/p' "${INSTALL_DIR}/speedtest-web/assets/js/config.js" 2>/dev/null | head -1)
    [ -n "$HTTP_PORT" ] || HTTP_PORT=24080
fi
case "$HTTP_PORT" in
    ''|*[!0-9]*) echo "Invalid --speed-test-http-port: $HTTP_PORT" >&2; exit 1 ;;
esac

# Git ref the speed test page and nginx config are fetched from. Testing a branch's installer
# against its own assets: AGENT_ASSETS_REF=<branch or commit>. Default main.
ASSETS_REF="${AGENT_ASSETS_REF:-main}"
case "$ASSETS_REF" in
    *[!A-Za-z0-9._/-]*|'') echo "Invalid AGENT_ASSETS_REF: $ASSETS_REF" >&2; exit 1 ;;
esac

# --release pins the binaries to one release (the one the server runs) instead of GitHub's
# "latest", which skips prereleases: a preview server would otherwise install a stable agent.
if [ -n "$RELEASE_TAG" ]; then
    case "$RELEASE_TAG" in
        v*) ;;
        *) RELEASE_TAG="v${RELEASE_TAG}" ;;
    esac
    case "$RELEASE_TAG" in
        *[!A-Za-z0-9.-]*) echo "Invalid --release tag: $RELEASE_TAG" >&2; exit 1 ;;
    esac
    RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/download/${RELEASE_TAG}"
fi

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

# UniFi only runs iperf3.service (nothing in UniFi configures it), so the speed test takes 5201
# over for the agent's own iperf3 server, which also captures client results. Masked, because
# the unit has Restart=always.
# Its prior state is recorded so removal puts it back exactly as found.
IPERF3_STATE="${INSTALL_DIR}/unifi-iperf3.state"
take_over_unifi_iperf3() {
    systemctl cat iperf3.service >/dev/null 2>&1 || return 0
    if [ ! -f "$IPERF3_STATE" ]; then
        printf 'enabled=%s\nactive=%s\n' \
            "$(systemctl is-enabled iperf3.service 2>/dev/null || true)" \
            "$(systemctl is-active iperf3.service 2>/dev/null || true)" > "$IPERF3_STATE"
    fi
    systemctl stop iperf3.service 2>/dev/null || true
    systemctl mask --quiet iperf3.service 2>/dev/null || true
}
restore_unifi_iperf3() {
    [ -f "$IPERF3_STATE" ] || return 0
    systemctl unmask --quiet iperf3.service 2>/dev/null || true
    grep -q '^enabled=enabled$' "$IPERF3_STATE" && systemctl enable --quiet iperf3.service 2>/dev/null || true
    grep -q '^active=active$' "$IPERF3_STATE" && systemctl start iperf3.service 2>/dev/null || true
    rm -f "$IPERF3_STATE"
}

# Removes the speed test nginx and its files; leaves the agent and agent.json alone.
remove_speedtest() {
    systemctl stop "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    systemctl disable --quiet "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    rm -f "/etc/systemd/system/${SPEEDTEST_SERVICE}.service"
    systemctl daemon-reload 2>/dev/null || true
    systemctl reset-failed "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    rm -rf "${INSTALL_DIR}/speedtest-web" "${INSTALL_DIR}/speedtest-tls" "${INSTALL_DIR}/nginx-temp"
    rm -f "${INSTALL_DIR}/nginx-speedtest.conf" "${INSTALL_DIR}/nginx-speedtest-server.conf" \
          "${INSTALL_DIR}/nginx.pid" "${INSTALL_DIR}/nginx-error.log"
}

[ "$(id -u)" -eq 0 ] || err "Run as root (the gateway's default SSH user is root)."
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)."

# --- Teardown --------------------------------------------------------------
if [ "$UNINSTALL" = true ]; then
    step "Removing the Network Optimizer agent"
    note "${SERVICE_NAME} + ${INSTALL_DIR}"
    remove_speedtest
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
    # Automatic Blocking's rules (chain, jumps, sets) outlive the agent otherwise; nothing else uses these names.
    for t in iptables ip6tables; do
        command -v "$t" >/dev/null 2>&1 || continue
        for c in FORWARD INPUT; do
            while "$t" -D "$c" -j NETOPT_BLOCK 2>/dev/null; do :; done
        done
        "$t" -F NETOPT_BLOCK 2>/dev/null || true
        "$t" -X NETOPT_BLOCK 2>/dev/null || true
    done
    if command -v ipset >/dev/null 2>&1; then
        for s in netopt_block_v4 netopt_block_v6 netopt_guard_v4 netopt_guard_v6 netopt_out_v4 netopt_out_v6; do
            ipset destroy "${s}_tmp" 2>/dev/null || true
            ipset destroy "$s" 2>/dev/null || true
        done
    fi
    # WAN Flood Protection's rules: the mangle chains and their jumps, the guard sets, our tc filters
    # (priorities 100-163 and 200-263), and an ingress qdisc the agent added and left empty. UniFi's own
    # filters and qdiscs are never touched. Reads wan-flood.json, so this runs before the dir is removed.
    for t in iptables ip6tables; do
        command -v "$t" >/dev/null 2>&1 || continue
        for c in PREROUTING FORWARD; do
            # || true: with no flood rules grep exits 1, and under pipefail set -e would end the uninstall.
            "$t" -w -t mangle -S "$c" 2>/dev/null | grep -E -e '-j NETOPT_FLOOD(_FWD)?$' | sed 's/^-A/-D/' \
                | while read -r r; do "$t" -w -t mangle $r 2>/dev/null; done || true
        done
        for ch in NETOPT_FLOOD NETOPT_FLOOD_FWD NETOPT_PROBE; do
            "$t" -w -t mangle -F "$ch" 2>/dev/null || true
            "$t" -w -t mangle -X "$ch" 2>/dev/null || true
        done
    done
    if command -v ipset >/dev/null 2>&1; then
        for s in netopt_flood_guard_v4 netopt_flood_guard_v6 netopt_probe; do
            ipset destroy "${s}_tmp" 2>/dev/null || true
            ipset destroy "$s" 2>/dev/null || true
        done
    fi
    if command -v tc >/dev/null 2>&1; then
        # Guarded by -f: under pipefail a sed on a missing file would fail the assignment and exit.
        owned=""
        if [ -f "${INSTALL_DIR}/wan-flood.json" ]; then
            owned=$(sed -n 's/.*"ownedQdiscs":\[\([^]]*\)\].*/\1/p' "${INSTALL_DIR}/wan-flood.json" | tr -d '"' | tr ',' ' ')
        fi
        for d in $(ls /sys/class/net 2>/dev/null); do
            tc qdisc show dev "$d" ingress 2>/dev/null | grep -q 'ingress ffff:' || continue
            for p in $(tc filter show dev "$d" parent ffff: 2>/dev/null | grep -o 'pref [0-9]*' | awk '{print $2}' | sort -u); do
                if { [ "$p" -ge 100 ] && [ "$p" -le 163 ]; } || { [ "$p" -ge 200 ] && [ "$p" -le 263 ]; }; then
                    tc filter del dev "$d" parent ffff: pref "$p" 2>/dev/null || true
                fi
            done
            for o in $owned; do
                if [ "$o" = "$d" ] && [ -z "$(tc filter show dev "$d" parent ffff: 2>/dev/null)" ]; then
                    tc qdisc del dev "$d" ingress 2>/dev/null || true
                fi
            done
        done
    fi
    ip link del netopt_probe 2>/dev/null || true
    systemctl stop netopt-flood-cleanup.timer netopt-flood-cleanup.service 2>/dev/null || true
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload 2>/dev/null || true
    systemctl reset-failed "${SERVICE_NAME}.service" 2>/dev/null || true
    # After the agent is gone, so its iperf3 child no longer holds 5201.
    restore_unifi_iperf3
    rm -rf "$INSTALL_DIR"
    if agent_running; then
        err "Agent process is STILL running after teardown (PID(s): $(agent_pids | tr '\n' ' ')). Kill it manually (e.g. kill -9 <pid>) and re-run --uninstall."
    fi
    ok "Removed - the gateway is back to stock."
    printf '\n'
    exit 0
fi

# --- Speed test removal (agent stays) -------------------------------------------
if [ "$NO_LAN_SPEED_TEST" = true ]; then
    step "Removing the LAN speed test"
    remove_speedtest
    if [ -f "${INSTALL_DIR}/agent.json" ]; then
        sed -i 's/"lanSpeedTest":[[:space:]]*true/"lanSpeedTest": false/' "${INSTALL_DIR}/agent.json"
    fi
    # The restart drops the agent's iperf3 server, which frees 5201 for UniFi's again.
    if systemctl is-enabled --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
        systemctl restart "${SERVICE_NAME}.service"
    fi
    restore_unifi_iperf3
    ok "LAN speed test removed; the agent keeps running."
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
MIN_AVAILABLE_MB=0
systemctl is-active --quiet "${SERVICE_NAME}.service" || MIN_AVAILABLE_MB=256
# The speed test nginx is fenced at MemoryHigh=256M, and holds ~200 MB of upload buffers after
# its first test, so it needs its own headroom on top of the agent's.
if [ "$LAN_SPEED_TEST" = true ] && ! systemctl is-active --quiet "${SPEEDTEST_SERVICE}.service"; then
    MIN_AVAILABLE_MB=$((MIN_AVAILABLE_MB + 256))
fi
if [ "$MIN_AVAILABLE_MB" -gt 0 ]; then
    step "Memory pre-flight"
    AVAILABLE_MB="$(awk '/MemAvailable/ {print int($2/1024)}' /proc/meminfo)"
    if [ -z "$AVAILABLE_MB" ]; then
        warn "could not read MemAvailable from /proc/meminfo - skipping the memory check."
    elif [ "$AVAILABLE_MB" -lt "$MIN_AVAILABLE_MB" ]; then
        if [ "$LAN_SPEED_TEST" = true ]; then
            err "only ${AVAILABLE_MB} MB of memory is available; the agent plus the LAN speed test need ${MIN_AVAILABLE_MB} MB of headroom so they stay well clear of routing/IPS. Re-run without --lan-speed-test, or run the agent on a separate box (see install-native.sh)."
        fi
        err "only ${AVAILABLE_MB} MB of memory is available; the agent needs ${MIN_AVAILABLE_MB} MB of headroom so it stays well clear of routing/IPS. Free up memory (e.g. remove unused UniFi applications) or run the agent on a separate box (see install-native.sh)."
    else
        ok "${AVAILABLE_MB} MB available (need ${MIN_AVAILABLE_MB} MB)"
    fi
fi

if [ "$LAN_SPEED_TEST" = true ]; then
    MODE_LABEL="gateway, with LAN speed test"
else
    MODE_LABEL="gateway, monitoring-only"
fi
printf '\n%sNetwork Optimizer on-site agent (%s)%s\n' "$_b" "$MODE_LABEL" "$_rst"
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
    # Unlike install-native.sh, the speed test is normally ADDED to an enrolled gateway agent,
    # so the switch and the port are written into the kept config here.
    if [ "$LAN_SPEED_TEST" = true ]; then
        if grep -q '"lanSpeedTest"' "$CONFIG"; then
            sed -i 's/"lanSpeedTest":[[:space:]]*false/"lanSpeedTest": true/' "$CONFIG"
        else
            sed -i '0,/{/s/{/{\n  "lanSpeedTest": true,/' "$CONFIG"
        fi
        if grep -q '"lanSpeedTestPort"' "$CONFIG"; then
            sed -i "s/\"lanSpeedTestPort\":[[:space:]]*[0-9]*/\"lanSpeedTestPort\": ${SPEED_TEST_PORT}/" "$CONFIG"
        elif [ "$SPEED_TEST_PORT" != "24443" ]; then
            sed -i "0,/{/s/{/{\n  \"lanSpeedTestPort\": ${SPEED_TEST_PORT},/" "$CONFIG"
        fi
        grep -q '"lanSpeedTest":[[:space:]]*true' "$CONFIG" || err "could not enable lanSpeedTest in ${CONFIG}"
    fi
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install."
    {
        echo "{"
        echo "  \"serverUrl\": \"${SERVER%/}\","
        echo "  \"tunnelUrl\": \"${SERVER%/}\","
        echo "  \"enrollmentToken\": \"${TOKEN}\","
        echo "  \"onGateway\": true,"
        printf '  "ignoreSslErrors": %s' "$INSECURE"
        if [ "$LAN_SPEED_TEST" = true ]; then
            printf ',\n  "lanSpeedTest": true'
            [ "$SPEED_TEST_PORT" != "24443" ] && printf ',\n  "lanSpeedTestPort": %s' "$SPEED_TEST_PORT"
        fi
        printf '\n}\n'
    } > "$CONFIG"
    chmod 600 "$CONFIG"
    ok "Wrote ${CONFIG}"
fi

# Same layout as install-native.sh --lan-speed-test (own nginx master, webroot, cert, unit;
# page and nginx config fetched from AGENT_ASSETS_REF), with gateway limits applied on top. UniFi's nginx is
# never touched: we only borrow its binary.
if [ "$LAN_SPEED_TEST" = true ]; then
    step "Setting up the LAN speed test (nginx)"
    NGINX_BIN="$(command -v nginx 2>/dev/null || echo /usr/sbin/nginx)"
    [ -x "$NGINX_BIN" ] || err "nginx not found on this gateway - re-run without --lan-speed-test."
    # Ours is stopped here (the agent restart below would stop it anyway), so a port still in
    # use belongs to something else.
    systemctl stop "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    for p in "$SPEED_TEST_PORT" 3002; do
        if ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${p}\$"; then
            err "port ${p} is already in use on this gateway - pick another with --speed-test-port, or re-run without --lan-speed-test."
        fi
    done
    # A taken handoff port only costs the handoff, never the install (upgrade re-runs included).
    if [ "$HTTP_PORT" != "0" ] && ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${HTTP_PORT}\$"; then
        warn "port ${HTTP_PORT} is in use on this gateway - plain HTTP handoff off, the test stays on HTTPS (pick another with --speed-test-http-port)."
        HTTP_PORT=0
        HTTP_PORT_NOTE=" // off: port in use, rechecked on the next install run"
    fi
    find /data -maxdepth 0 -perm -o+x | grep -q . \
        || err "/data is not world-traversable, so nginx's unprivileged workers cannot reach the page."

    WEBROOT="${INSTALL_DIR}/speedtest-web"
    RAW="https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/${ASSETS_REF}"
    mkdir -p "$WEBROOT/assets/js"
    note "Fetching the OpenSpeedTest page"
    # Streamed, never saved: /tmp is RAM on a gateway and the repo tarball is large.
    TMPX="$(mktemp -d)"
    # GitHub names the archive's top dir after the ref, with / turned into -.
    curl -fsSL "https://github.com/Ozark-Connect/NetworkOptimizer/archive/${ASSETS_REF}.tar.gz" \
        | tar -xzf - -C "$TMPX" --strip-components=3 "NetworkOptimizer-$(echo "$ASSETS_REF" | tr '/' '-')/src/OpenSpeedTest"
    cp -r "$TMPX/." "$WEBROOT/"
    rm -rf "$TMPX"
    cat > "$WEBROOT/assets/js/config.js" <<'CFGJS'
var saveData = true;
var saveDataURL = window.location.protocol + "//" + window.location.host + "/api/public/speedtest/results";
var apiPath = "/api/public/speedtest/results";
var externalServerId = "";
var clientResultsUrl = window.location.protocol + "//" + window.location.host + "/client-speedtest";
var OpenSpeedTestdb = "";
CFGJS
    echo "var plainHttpPort = ${HTTP_PORT};${HTTP_PORT_NOTE}" >> "$WEBROOT/assets/js/config.js"
    chmod 755 "$INSTALL_DIR"
    chmod -R a+rX "$WEBROOT"

    curl -fsSL "$RAW/docker/agent/nginx.conf" -o "${INSTALL_DIR}/nginx-speedtest-server.conf"
    sed -i "s#root /usr/share/nginx/html;#root ${WEBROOT};#" "${INSTALL_DIR}/nginx-speedtest-server.conf"
    if [ "$SPEED_TEST_PORT" != "24443" ]; then
        sed -i "/listen/ s/24443/${SPEED_TEST_PORT}/" "${INSTALL_DIR}/nginx-speedtest-server.conf"
    fi

    # The plain HTTP twin listener the HTTPS page hands the test to (0 removes it).
    if [ "$HTTP_PORT" = "0" ]; then
        sed -i '/listen.*24080/d' "${INSTALL_DIR}/nginx-speedtest-server.conf"
        note "Plain HTTP handoff off - the whole test runs on HTTPS"
    elif [ "$HTTP_PORT" != "24080" ]; then
        sed -i "/listen/ s/24080/${HTTP_PORT}/" "${INSTALL_DIR}/nginx-speedtest-server.conf"
        note "Plain HTTP handoff on port ${HTTP_PORT}"
    fi

    # SANs are the LAN bridge addresses only: hostname -I on a gateway includes the WAN IP.
    CERTDIR="${INSTALL_DIR}/speedtest-tls"
    mkdir -p "$CERTDIR"
    if [ ! -f "$CERTDIR/cert.pem" ]; then
        LAN_IPS=$(ip -4 -o addr show 2>/dev/null | awk '$2 ~ /^br/ {sub("/.*", "", $4); print $4}')
        SAN="DNS:$(hostname),DNS:localhost"
        for ip in $LAN_IPS; do SAN="$SAN,IP:$ip"; done
        CN=$(echo "$LAN_IPS" | head -n 1)
        openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
            -keyout "$CERTDIR/key.pem" -out "$CERTDIR/cert.pem" \
            -subj "/CN=${CN:-agent}" -addext "subjectAltName=$SAN" >/dev/null 2>&1 \
            || err "self-signed cert generation failed."
        chmod 600 "$CERTDIR/key.pem"
    fi
    sed -i \
        -e "s#__CERTFILE__#${CERTDIR}/cert.pem#" \
        -e "s#__KEYFILE__#${CERTDIR}/key.pem#" \
        "${INSTALL_DIR}/nginx-speedtest-server.conf"

    # Gateway limits: one worker per core up to 4 (4 measured faster than 2 on a 4-core gateway;
    # the unit's memory fence bounds the rest), and nobody (some gateways have no nginx account,
    # and where one exists it is UniFi's).
    WORKERS=$(nproc 2>/dev/null || echo 2)
    [ "$WORKERS" -gt 4 ] && WORKERS=4
    # Install-time override for tuning (AGENT_SPEEDTEST_WORKERS=N); not persisted.
    case "${AGENT_SPEEDTEST_WORKERS:-}" in
        '') ;;
        *[!0-9]*|0) err "AGENT_SPEEDTEST_WORKERS must be a positive integer" ;;
        *) WORKERS="$AGENT_SPEEDTEST_WORKERS" ;;
    esac
    curl -fsSL "$RAW/docker/agent/nginx-standalone.conf" -o "${INSTALL_DIR}/nginx-speedtest.conf"
    sed -i \
        -e "s#__PIDFILE__#${INSTALL_DIR}/nginx.pid#" \
        -e "s#__ERRORLOG__#${INSTALL_DIR}/nginx-error.log#" \
        -e "s#__SERVERCONF__#${INSTALL_DIR}/nginx-speedtest-server.conf#" \
        -e "s#^worker_processes auto;#worker_processes ${WORKERS};#" \
        "${INSTALL_DIR}/nginx-speedtest.conf"
    sed -i "1i user nobody nogroup;" "${INSTALL_DIR}/nginx-speedtest.conf"
    # Never UniFi's: the borrowed binary's compiled-in temp paths are /var/cache/nginx/*_temp,
    # and a master re-owns those to its worker user at start, which locks UniFi's nginx out of
    # buffering every authenticated /proxy/network response.
    TEMPDIR="${INSTALL_DIR}/nginx-temp"
    mkdir -p "$TEMPDIR"
    sed -i "s#^http {#http {\n    client_body_temp_path ${TEMPDIR}/client_body;\n    proxy_temp_path ${TEMPDIR}/proxy;\n    fastcgi_temp_path ${TEMPDIR}/fastcgi;\n    uwsgi_temp_path ${TEMPDIR}/uwsgi;\n    scgi_temp_path ${TEMPDIR}/scgi;#" \
        "${INSTALL_DIR}/nginx-speedtest.conf"
    # Fail closed before nginx ever starts: every temp path the binary was built with, plus the
    # five every build has (unlisted ones default under --prefix), must point at our dir. A
    # firmware whose nginx adds one we do not override stops here instead of re-owning it.
    TEMP_DIRECTIVES="client_body_temp_path proxy_temp_path fastcgi_temp_path uwsgi_temp_path scgi_temp_path"
    for opt in $("$NGINX_BIN" -V 2>&1 | tr ' ' '\n' | sed -n 's/^--http-\(.*\)-temp-path=.*/\1/p'); do
        TEMP_DIRECTIVES="$TEMP_DIRECTIVES $(echo "$opt" | tr '-' '_')_temp_path"
    done
    for d in $(echo "$TEMP_DIRECTIVES" | tr ' ' '\n' | sort -u); do
        grep -qE "^[[:space:]]*${d}[[:space:]]+${TEMPDIR}/" "${INSTALL_DIR}/nginx-speedtest.conf" \
            || err "this gateway's nginx has a temp path (${d}) the speed test does not override, so starting it would take that directory from UniFi's nginx. Re-run without --lan-speed-test."
    done

    cat > "/etc/systemd/system/${SPEEDTEST_SERVICE}.service" <<UNIT
[Unit]
Description=Network Optimizer LAN speed test (nginx)
# Results relay through the agent, so nginx stops with it (BindsTo) and starts with it
# (WantedBy below), as in install-native.sh.
After=network-online.target ${SERVICE_NAME}.service
Wants=network-online.target
BindsTo=${SERVICE_NAME}.service

[Service]
Type=forking
PIDFile=${INSTALL_DIR}/nginx.pid
ExecStartPre=${NGINX_BIN} -t -q -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecStart=${NGINX_BIN} -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecReload=${NGINX_BIN} -s reload -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecStop=${NGINX_BIN} -s quit -c ${INSTALL_DIR}/nginx-speedtest.conf
# Upload buffers stay resident after the first test (~200 MB); the fence keeps that
# well clear of routing/IPS. LimitNOFILE matches worker_connections.
MemoryHigh=256M
MemoryMax=384M
LimitNOFILE=8192
Restart=always
RestartSec=5

[Install]
WantedBy=${SERVICE_NAME}.service
UNIT

    # Under the unit's file limit: the SSH shell's 1024 warns about worker_connections.
    ( ulimit -n 8192 2>/dev/null || true; "$NGINX_BIN" -t -q -c "${INSTALL_DIR}/nginx-speedtest.conf" ) \
        || err "nginx config test failed. Diagnose: $NGINX_BIN -t -c ${INSTALL_DIR}/nginx-speedtest.conf"
    systemctl daemon-reload
    systemctl reenable --quiet "${SPEEDTEST_SERVICE}.service"
    take_over_unifi_iperf3
    ok "LAN speed test ready on port ${SPEED_TEST_PORT} (starts with the agent)"
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

# BindsTo stopped nginx with the agent restart; start it now the agent is up.
if [ "$LAN_SPEED_TEST" = true ]; then
    systemctl start "${SPEEDTEST_SERVICE}.service"
    ok "${SPEEDTEST_SERVICE}.service started"
fi

step "Done"
ok "Agent installed and running (${MODE_LABEL#gateway, })"
note "It enrolls, then holds a tunnel to ${SERVER%/} - watch it come Online in the web UI."
note "Logs:   journalctl -u ${SERVICE_NAME} -f"
if [ "$LAN_SPEED_TEST" = true ]; then
    note "A running speed test loads the gateway's CPU for its duration."
    note "Remove the speed test only: curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- --no-lan-speed-test"
fi
note "Remove: curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- --uninstall"
printf '\n'
