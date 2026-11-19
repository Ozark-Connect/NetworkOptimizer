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
#   --lan-speed-test Host the LAN speed test page (port 24443, test on plain HTTP 24080) and iperf3 (5201)
#   --insecure       Accept a self-signed cert on the server's reverse proxy
#   --release TAG    Run this release's image (e.g. v2.9.1-preview1) instead of the latest stable one
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
RELEASE_TAG=""

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --lan-speed-test) LAN_SPEED_TEST=true; shift ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        --release) RELEASE_TAG="$2"; shift 2 ;;
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

# --- Server pre-flight --------------------------------------------------------
# Probes the tunnel route the agent uses (the agent README's "Verify the connection" call). It stops
# an install in one case only: the certificate is untrusted while -k reaches a working tunnel, so the
# agent could never connect. Anything else warns or skips, so a curl that cannot run the probe never
# blocks an install. Each curl runs on the left of && / ||, where set -e does not apply.
PREFLIGHT_DOC="https://github.com/Ozark-Connect/NetworkOptimizer/blob/main/src/NetworkOptimizer.Agent/README.md#verify-the-connection"
# The stop's exit status. Run It for Me and the Proxmox installer read it to offer --insecure.
PREFLIGHT_UNTRUSTED_EXIT=3
# $1: the base URL. The rest are extra curl arguments.
tunnel_probe() {
    local base="$1"; shift
    curl "$@" -sS -D - -o /dev/null --http2 --max-time 10 -X POST -H 'content-type: application/grpc' \
        "${base%/}/networkoptimizer.agent.v1.AgentTunnel/Connect" 2>&1
}
tunnel_verdict() {
    # Here-strings, not pipes: under pipefail an early-exiting grep -q can fail the pipe on a match.
    # No -i: GNU grep 3.0 aborts on -iF. HTTP/2 headers are lowercase, HTTP/1.1 ones capitalized.
    if grep -qE '[Gg]rpc-[Mm]essage: First message must be a hello' <<<"$1"; then echo hello
    elif grep -qE '[Gg]rpc-[Mm]essage: Service is unimplemented' <<<"$1"; then echo unimplemented
    # The app's own answer when a proxy forwards the tunnel path to it over HTTP/1.1.
    elif grep -qE "Request protocol 'HTTP/1\.1' is not supported" <<<"$1"; then echo unimplemented
    elif grep -qE '^HTTP/[0-9.]+ 404' <<<"$1" && ! grep -qE '^[Gg]rpc-[Mm]essage:' <<<"$1"; then echo notfound
    else echo other
    fi
}
# What a failed probe says about the route, or nothing when it says nothing. $1 output, $2 curl exit, $3 base URL.
route_problem() {
    local code
    case "$2" in
        6|7|28) echo "Could not reach ${3%/} (curl exit $2). The install continues and the agent keeps retrying."; return 0 ;;
        35) echo "The TLS handshake with ${3%/} failed (curl exit 35). Check that the URL points at the proxy's HTTPS port."; return 0 ;;
        52|56) echo "The connection to ${3%/} closed without an answer (curl exit $2)."; return 0 ;;
        60|51) return 0 ;;
    esac
    case "$(tunnel_verdict "$1")" in
        unimplemented) echo "The proxy sends the tunnel path to the app, not the tunnel port: add the gRPC route." ;;
        notfound) echo "The proxy has no route for the tunnel path at ${3%/} (HTTP 404)." ;;
        *)
            code=$(sed -n 's#^HTTP/[0-9.]* \([0-9][0-9]*\).*#\1#p' <<<"$1") || code=""
            code=${code##*$'\n'}
            if [ -n "$code" ]; then echo "The tunnel route did not answer at ${3%/} (HTTP ${code})."; fi
            ;;
    esac
    return 0
}
# $1: the agent.json path. $2: "stop" when this installer may stop on an untrusted certificate.
preflight_server() {
    local config="$1" may_stop="$2" enrolled=false tls="" base="$SERVER" out rc verdict version saved msg
    step "Checking the server"
    version=$(curl --version 2>/dev/null) || version=""
    if ! grep -q 'HTTP2' <<<"$version"; then
        note "Skipped: this host's curl has no HTTP/2 support, so it cannot test the tunnel route."
        return 0
    fi
    if ${SUDO:-} grep -q '"agentKey"' "$config" 2>/dev/null; then
        enrolled=true
        # An enrolled agent keeps its agent.json, so probe the URL it actually dials.
        saved=$(${SUDO:-} sed -n 's/^[[:space:]]*"tunnelUrl":[[:space:]]*"\([^"]*\)".*/\1/p' "$config" 2>/dev/null) || saved=""
        saved=${saved%%$'\n'*}
        case "$saved" in https://*) base="$saved" ;; esac
    fi
    if [ "$INSECURE" = true ] || ${SUDO:-} grep -q '"ignoreSslErrors":[[:space:]]*true' "$config" 2>/dev/null; then
        tls="-k"
    fi
    out=$(tunnel_probe "$base" $tls) && rc=0 || rc=$?
    verdict=$(tunnel_verdict "$out")
    if [ "$verdict" = hello ]; then
        ok "The tunnel route answers at ${base%/}"
        return 0
    fi
    case "$rc" in
        60|51)
            if [ -z "$tls" ]; then
                out=$(tunnel_probe "$base" -k) && rc=0 || rc=$?
                if [ "$(tunnel_verdict "$out")" = hello ]; then
                    if [ "$enrolled" = true ]; then
                        warn "This host does not trust the server's certificate. If you issued it yourself, set \"ignoreSslErrors\": true in ${config} and restart the agent. Otherwise fix the certificate on the reverse proxy."
                        return 0
                    fi
                    # The stop needs this host's trust store to work, or a broken store would read as a bad certificate.
                    if [ "$may_stop" = stop ] && curl -sS -o /dev/null --max-time 10 https://github.com >/dev/null 2>&1; then
                        printf '%sError:%s %s\n' "${_red}${_b}" "$_rst" "This host does not trust the server's certificate, so the agent cannot connect to ${base%/}. If you issued the certificate yourself, re-run this command with --insecure. Otherwise fix the certificate on the reverse proxy. See ${PREFLIGHT_DOC}" >&2
                        exit "$PREFLIGHT_UNTRUSTED_EXIT"
                    fi
                    warn "This host does not trust the server's certificate. If you issued it yourself, re-run this command with --insecure. Otherwise fix the certificate on the reverse proxy."
                    return 0
                fi
            fi
            # The route problem behind the certificate one, from the -k probe when it ran.
            warn "This host does not trust the server's certificate."
            ;;
    esac
    msg=$(route_problem "$out" "$rc" "$base")
    if [ -n "$msg" ]; then
        warn "$msg See ${PREFLIGHT_DOC}"
    else
        note "Inconclusive (curl exit ${rc}); continuing."
    fi
    return 0
}

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
# Warn only: the agent checks the certificate inside its image, whose trust store is not this host's.
preflight_server "${INSTALL_DIR}/data/agent.json" warn
$SUDO mkdir -p "${INSTALL_DIR}/data"

step "Fetching the compose template"
COMPOSE_FILE="${INSTALL_DIR}/docker-compose.yml"
TMP_COMPOSE="$(mktemp)"
TMP_PAIRS="$(mktemp)"
trap 'rm -f "$TMP_COMPOSE" "$TMP_PAIRS" "${TMP_COMPOSE}.merged"' EXIT
curl -fsSL "$COMPOSE_URL" -o "$TMP_COMPOSE"
# The template is refreshed on every run, so an upgrade carries the user's environment settings
# (every active "- KEY=VALUE" line) into it: a key the template has, active or commented out, takes
# the user's line in place; any other key goes under environment:. [ \t] rather than [[:space:]]
# because mawk 1.3.3 (older Debian/Ubuntu) has no character classes.
if [ -f "$COMPOSE_FILE" ]; then
    printf '#\n' > "$TMP_PAIRS"   # header line, so FNR == 1 fires even when no keys are set
    $SUDO awk '/^[ \t]*-[ \t]*[A-Za-z_][A-Za-z0-9_]*=/ { sub(/^[ \t]*-[ \t]*/, ""); print }' "$COMPOSE_FILE" >> "$TMP_PAIRS"
    awk '
        function keyof(s) { sub(/^[ \t]*#?[ \t]*-[ \t]*/, "", s); sub(/=.*/, "", s); return s }
        FNR == 1 { f++ }
        f == 1 { if ($0 !~ /^#/) { k = keyof($0); if (!(k in val)) { val[k] = $0; ord[++n] = k } } next }
        f == 2 { if ($0 ~ /^[ \t]*#?[ \t]*-[ \t]*[A-Za-z_][A-Za-z0-9_]*=/) intpl[keyof($0)] = 1; next }
        {
            if ($0 ~ /^[ \t]*#?[ \t]*-[ \t]*[A-Za-z_][A-Za-z0-9_]*=/) {
                k = keyof($0)
                if ((k in val) && !(k in done)) {
                    ind = $0; sub(/[^ \t].*$/, "", ind); print ind "- " val[k]; done[k] = 1; next
                }
            }
            print
            if ($0 ~ /^[ \t]*environment:[ \t]*$/) {
                ind = $0; sub(/[^ \t].*$/, "", ind)
                for (i = 1; i <= n; i++) if (!(ord[i] in intpl)) { print ind "  - " val[ord[i]]; done[ord[i]] = 1 }
            }
        }' "$TMP_PAIRS" "$TMP_COMPOSE" "$TMP_COMPOSE" > "${TMP_COMPOSE}.merged"
    mv "${TMP_COMPOSE}.merged" "$TMP_COMPOSE"
fi
# --release pins the image to one release (the one the server runs) instead of :latest, which
# only moves on stable releases: a preview server would otherwise run a stable agent.
if [ -n "$RELEASE_TAG" ]; then
    case "$RELEASE_TAG" in
        *[!A-Za-z0-9.-]*) err "Invalid --release tag: $RELEASE_TAG" ;;
    esac
    IMAGE_TAG="${RELEASE_TAG#v}"
    sed -i "s#\(image: ghcr.io/ozark-connect/agent\):latest#\1:${IMAGE_TAG}#" "$TMP_COMPOSE"
    grep -q "agent:${IMAGE_TAG}" "$TMP_COMPOSE" || err "Could not pin the image to ${IMAGE_TAG}"
fi
# Anything outside environment: (ports, volumes, other edits) is not carried, so keep the old file.
if [ -f "$COMPOSE_FILE" ] && ! $SUDO cmp -s "$COMPOSE_FILE" "$TMP_COMPOSE"; then
    $SUDO cp -p "$COMPOSE_FILE" "${COMPOSE_FILE}.bak"
    note "Previous compose file saved to ${COMPOSE_FILE}.bak (environment settings carried over)"
fi
$SUDO cp "$TMP_COMPOSE" "$COMPOSE_FILE"
$SUDO chmod 644 "$COMPOSE_FILE"
ok "docker-compose.yml"
if [ -n "$RELEASE_TAG" ]; then
    ok "Image pinned to agent:${IMAGE_TAG}"
fi

CONFIG="${INSTALL_DIR}/data/agent.json"

step "Configuring the agent"
# Preserve an already-enrolled config so re-running the installer (e.g. to update the
# image) never wipes the persisted agent key. A token on top of an enrolled config
# re-enrolls ONLY when the server no longer accepts the existing key (agent removed or
# disabled): a saved install command carries its original, already-used token, and
# re-running it is how people upgrade, so discarding a working key on that would loop
# the agent on a dead token. The probe is the heartbeat every agent version already sends.
FRESH_CONFIG=false
if $SUDO grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    if [ -n "$TOKEN" ]; then
        CURRENT_KEY=$($SUDO sed -n 's/^[[:space:]]*"agentKey":[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG" | head -n 1)
        SERVER_URL=$($SUDO sed -n 's/^[[:space:]]*"serverUrl":[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG" | head -n 1)
        [ -n "$SERVER_URL" ] || SERVER_URL="$SERVER"
        CURL_TLS=""
        if [ "$INSECURE" = true ] || $SUDO grep -q '"ignoreSslErrors":[[:space:]]*true' "$CONFIG"; then CURL_TLS="-k"; fi
        KEY_STATUS=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 $CURL_TLS \
            -H 'Content-Type: application/json' -d "{\"agentKey\":\"${CURRENT_KEY}\"}" \
            "${SERVER_URL%/}/api/public/agents/heartbeats" 2>/dev/null || true)   # unreachable: 000, not a set -e exit
        case "$KEY_STATUS" in
            204)
                note "Existing enrollment is still valid - keeping the agent key (the token is not needed)"
                ;;
            401)
                FRESH_CONFIG=true
                note "The server no longer accepts this agent's key - re-enrolling with the token"
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
                ;;
            *)
                warn "Could not verify the existing key with ${SERVER_URL} (HTTP ${KEY_STATUS}) - keeping agent.json unchanged. Re-run once the server is reachable, or remove the agent in the app to force a fresh enrollment."
                ;;
        esac
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
# $SUDO is empty when the script itself runs under sudo, but the user still needs it for docker.
LOG_SUDO="${SUDO:+$SUDO }"
if [ -z "$LOG_SUDO" ] && [ -n "${SUDO_USER:-}" ] && [ "${SUDO_USER}" != root ]; then LOG_SUDO="sudo "; fi
note "Logs:   ${LOG_SUDO}docker logs -f network-optimizer-agent"
DIR_ARG=""
[ "$INSTALL_DIR" != "/opt/network-optimizer-agent" ] && DIR_ARG=" --dir \"${INSTALL_DIR}\""
note "Remove: curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-docker.sh | ${SUDO} bash -s -- --uninstall${DIR_ARG}"
printf '\n'
