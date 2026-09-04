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
#   --lan-speed-test Host the LAN speed test page (port 24443) and iperf3 (5201)
#   --speed-test-port N  Serve the LAN speed test page on N instead of 24443. The agent tells the
#                    server which port it uses, so in-app links follow automatically.
#   --insecure       Accept a self-signed cert on the server's reverse proxy
#   --force-native   Skip the UniFi gateway refusal (this installer targets a
#                    separate box; on a UniFi OS gateway use install-agent-gateway.sh)
#   --dir PATH       Install directory (default: /opt/netopt-agent)
#   --uninstall      Stop and remove the agent, its services, install dir, and any
#                    AppArmor override this installer added, then exit
#   --configure-apparmor  With --lan-speed-test: add a persistent AppArmor exception
#                    if the host's nginx profile blocks the speed test (off by default)

set -euo pipefail

SERVER=""
TOKEN=""
LAN_SPEED_TEST=false
SPEED_TEST_PORT=24443
PORT_REQUESTED=false
INSECURE=false
UNINSTALL=false
FORCE_NATIVE=false
CONFIGURE_APPARMOR=false
INSTALL_DIR="/opt/netopt-agent"
SERVICE_NAME="netopt-agent"
SPEEDTEST_SERVICE="netopt-speedtest-nginx"
# PREVIEW-CYCLE PIN (2.8.0) - REVERT BEFORE STABLE: fetch the newest v2.8.0-preview*
# binaries; releases/latest would hand back the pre-conntrack stable agent.
PREVIEW_TAG="$(curl -fsSL 'https://api.github.com/repos/Ozark-Connect/NetworkOptimizer/releases?per_page=20' 2>/dev/null | sed -n 's/.*"tag_name": *"\(v2\.8\.0-preview[0-9]*\)".*/\1/p' | head -n1)" || PREVIEW_TAG=""
RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/download/${PREVIEW_TAG:-v2.8.0-preview8}"

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="$2"; shift 2 ;;
        --token) TOKEN="$2"; shift 2 ;;
        --lan-speed-test) LAN_SPEED_TEST=true; shift ;;
        --speed-test-port) SPEED_TEST_PORT="$2"; PORT_REQUESTED=true; shift 2 ;;
        --insecure) INSECURE=true; shift ;;
        --uninstall) UNINSTALL=true; shift ;;
        --force-native) FORCE_NATIVE=true; shift ;;
        --configure-apparmor) CONFIGURE_APPARMOR=true; shift ;;
        --dir) INSTALL_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# An install that already serves the LAN speed test keeps serving it, whether or not the flag was
# repeated on this run. Without this, re-running to update the binary quietly stopped refreshing the
# nginx config while agent.json (deliberately preserved) kept the speed test switched on - so the two
# could end up disagreeing about the loopback relay port, and results would silently stop posting.
if [ "$LAN_SPEED_TEST" != true ] && grep -q '"lanSpeedTest"[[:space:]]*:[[:space:]]*true' "${INSTALL_DIR}/agent.json" 2>/dev/null; then
    LAN_SPEED_TEST=true
fi

# Likewise the port: an install already serving on one keeps it unless this run names a different
# one. Upgrading should not move a page that clients and firewall rules already know about, and an
# agent enrolled before the port was configurable has the old 3000 recorded. Deleting that line from
# agent.json is how an existing install adopts the current default.
if [ "$SPEED_TEST_PORT" = "24443" ] && [ -f "${INSTALL_DIR}/agent.json" ]; then
    EXISTING_PORT=$(sed -n 's/.*"lanSpeedTestPort"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "${INSTALL_DIR}/agent.json" | head -1)
    [ -n "$EXISTING_PORT" ] && SPEED_TEST_PORT="$EXISTING_PORT"
fi

# --- Output helpers -----------------------------------------------------------
# Colorized, structured output; colors collapse to empty when stdout isn't a
# terminal (piped/redirected/logged), so captured output stays clean.
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

# --- LAN speed test nginx: AppArmor remediation -------------------------------
# Some distros ship an ENFORCING AppArmor profile on the nginx binary that only
# permits nginx's stock paths (/var/log/nginx, /run/nginx.pid, ...). Our dedicated
# speed test master keeps its pid, logs, and webroot under the install dir, which
# such a profile denies even to root - the failure is a MAC denial, not file
# permissions. Everything below runs ONLY after `nginx -t` has already failed and
# only acts on a real AppArmor denial of our install dir, so any host where the
# speed test already works never enters this path.

# Markers bracketing our block inside an AppArmor local override, so it can be added
# without clobbering a user's existing file and removed cleanly on --uninstall.
AA_MARK_BEGIN="# NETOPT-AGENT-OVERRIDE BEGIN"
AA_MARK_END="# NETOPT-AGENT-OVERRIDE END"

# True when the kernel log shows AppArmor denying access to a path under the
# install dir (recorded when the failing `nginx -t` ran). This is the trigger:
# without an actual denial we leave AppArmor entirely alone.
_aa_denied_install_dir() {
    [ -d /sys/kernel/security/apparmor ] || return 1
    { journalctl -k -n 400 --no-pager 2>/dev/null || dmesg 2>/dev/null || true; } \
        | grep -q "apparmor=\"DENIED\".*name=\"${INSTALL_DIR}" 2>/dev/null
}

# The AppArmor profile NAME confining nginx (e.g. "/usr/bin/nginx"), read from the
# denial record. Empty if none was logged.
_aa_nginx_profile_name() {
    { journalctl -k -n 400 --no-pager 2>/dev/null || dmesg 2>/dev/null || true; } \
        | grep "apparmor=\"DENIED\".*name=\"${INSTALL_DIR}" \
        | grep -o 'profile="[^"]*"' | head -1 | sed 's/^profile="//; s/"$//' || true
}

# AppArmor's conventional on-disk filename for a profile name: drop the leading
# '/', turn the remaining '/' into '.' - e.g. /usr/bin/nginx -> usr.bin.nginx.
_aa_profile_filename() { local n="${1#/}"; printf '%s' "${n//\//.}"; }

# Path of the vendor AppArmor profile file confining nginx: the conventionally-named
# file first, else a content match, across the standard profile dirs. Empty when the
# profile isn't file-backed there (e.g. a snap profile) - then a local/ override
# can't apply and we fall back to the hint. Cached after the first lookup.
AA_NGINX_PROFILE_FILE=""
AA_NGINX_PROFILE_FILE_SET=""
_aa_nginx_profile_file() {
    [ -n "$AA_NGINX_PROFILE_FILE_SET" ] && { printf '%s' "$AA_NGINX_PROFILE_FILE"; return 0; }
    AA_NGINX_PROFILE_FILE_SET=1
    local pname fname dir cand file=""
    pname="$(_aa_nginx_profile_name)"; [ -n "$pname" ] || pname="$NGINX_BIN"
    fname="$(_aa_profile_filename "$pname")"
    for dir in /etc/apparmor.d /usr/share/apparmor.d /var/lib/snapd/apparmor/profiles; do
        [ -d "$dir" ] || continue
        if [ -f "$dir/$fname" ]; then
            cand="$dir/$fname"
        else
            # Exclude local/ (include-only) and cache/ (compiled binaries, not loadable
            # profile sources - matching one leads to a bogus apparmor_parser reload).
            cand="$(grep -rslF "$pname" "$dir" 2>/dev/null | grep -vE '/(local|cache)/' | head -1 || true)"
        fi
        [ -n "$cand" ] && { file="$cand"; break; }
    done
    AA_NGINX_PROFILE_FILE="$file"
    printf '%s' "$file"
}

# Additive, local-only AppArmor override letting the confined nginx use the install
# dir. OPT-IN: only runs under --configure-apparmor, so the installer never modifies
# host security policy unless explicitly asked. The override is scoped and persistent
# (a file under /etc/apparmor.d/local); never edits the vendor profile; reloads only
# that one profile. A parse error makes apparmor_parser abort and keep the running
# profile, so a bad override can't unconfine the host's own nginx. Caller re-tests
# `nginx -t` to judge the result.
maybe_fix_apparmor_nginx() {
    [ "$CONFIGURE_APPARMOR" = true ] || return 0
    command -v apparmor_parser >/dev/null 2>&1 || return 0
    _aa_denied_install_dir || return 0

    local file base
    file="$(_aa_nginx_profile_file)"
    case "$file" in
        # Vendor profiles under these dirs use the standard local/ include base
        # (/etc/apparmor.d/local), so an override there applies. The override file
        # always lives under /etc/apparmor.d/local regardless of where the profile
        # itself ships; we reload the profile source wherever it was found.
        /etc/apparmor.d/*|/usr/share/apparmor.d/*) ;;
        # Snap or otherwise non-standard profile won't consume /etc/apparmor.d/local:
        # a local override wouldn't apply. Leave it to the hint below.
        *) return 0 ;;
    esac
    base="$(basename "$file")"
    local localfile="/etc/apparmor.d/local/${base}"

    echo "AppArmor is blocking ${INSTALL_DIR}; adding a local override (${localfile}) and reloading."
    mkdir -p /etc/apparmor.d/local
    # Additive and idempotent: strip any prior block of ours (e.g. an earlier install
    # to a different dir), then append the current one. Never clobbers a user's own
    # rules in this file.
    [ -f "$localfile" ] && sed -i "/${AA_MARK_BEGIN}/,/${AA_MARK_END}/d" "$localfile"
    {
        echo "$AA_MARK_BEGIN"
        echo "# Added by the Network Optimizer agent installer - removed by --uninstall."
        echo "# Lets the LAN speed test nginx master use its pid, logs, and webroot here."
        echo "${INSTALL_DIR}/ r,"
        echo "${INSTALL_DIR}/** rw,"
        echo "$AA_MARK_END"
    } >> "$localfile"

    # -T skips the compiled cache, so a read-only cache dir (common on appliance
    # distros) doesn't fail the reload; the profile still loads from source.
    apparmor_parser -rT "$file" >/dev/null 2>&1 \
        || echo "  apparmor_parser reload failed; the override may not have taken (the profile may not include local/${base})."
    return 0
}

# Actionable manual remediation, printed only when the speed test nginx still fails
# because of an AppArmor denial (so it never nags on unrelated failures). By the time
# this prints, the automatic local-override fix has already been tried or wasn't
# possible, so complain mode - which works regardless of how the profile is shipped -
# is the reliable recommendation. Names the real profile.
print_speedtest_apparmor_hint() {
    _aa_denied_install_dir || return 0
    local pname
    pname="$(_aa_nginx_profile_name)"; [ -n "$pname" ] || pname="$NGINX_BIN"
    echo "  AppArmor profile '${pname}' denies nginx access to ${INSTALL_DIR}."
    echo "  The agent and monitoring are unaffected - this gates only the optional speed test page."
    if [ "$CONFIGURE_APPARMOR" != true ]; then
        echo "  To have the installer add a persistent, scoped AppArmor exception, re-run the"
        echo "  install command with  --configure-apparmor  added."
    else
        echo "  A scoped exception couldn't be applied here: this host's nginx profile has no"
        echo "  source file or local/ include hook to attach one to. Enabling the speed test"
        echo "  needs a persistent change to that profile by your admin, or serving it from a"
        echo "  host whose nginx isn't AppArmor-confined."
    fi
}

# Remove any AppArmor local override this installer added (marker-guarded, so a
# user's own rules in the same file are preserved), then best-effort reload so the
# removal takes effect. Called from teardown.
remove_apparmor_override() {
    local f changed=0
    for f in /etc/apparmor.d/local/*; do
        [ -f "$f" ] || continue
        grep -q "$AA_MARK_BEGIN" "$f" 2>/dev/null || continue
        sed -i "/${AA_MARK_BEGIN}/,/${AA_MARK_END}/d" "$f"
        [ -s "$f" ] || rm -f "$f"   # nothing left but our block -> drop the file
        changed=1
        note "Removed AppArmor override from ${f}"
    done
    [ "$changed" = 1 ] && systemctl reload apparmor >/dev/null 2>&1 || true
    AA_OVERRIDE_REMOVED="$changed"
    return 0
}

# nginx workers drop privileges after start, so an install dir whose ancestors are
# not world-traversable (e.g. under /root, mode 700) makes them 403 - they can't
# reach the webroot. True in that case, so the wrapper should run workers as root.
_webroot_needs_root_worker() {
    local p mode
    p="$(readlink -f "$1" 2>/dev/null || echo "$1")"
    while [ -n "$p" ] && [ "$p" != "/" ]; do
        mode="$(stat -c '%A' "$p" 2>/dev/null || echo '')"
        case "$mode" in
            "") ;;             # stat failed at this level; ignore
            *x|*t) ;;          # other-execute set (x, or t with the sticky bit as on
                               # /tmp) -> traversable, keep walking up
            *) return 0 ;;     # last char '-' or 'T' -> blocks an unprivileged worker
        esac
        p="$(dirname "$p")"
    done
    return 1
}

# Stop and remove everything this installer created, in an order that never leaves a
# running "ghost" unit (services are stopped before their unit files are deleted).
# Leaves the host's own nginx untouched; removes only the AppArmor override this
# installer itself added (if any), reporting accordingly.
# PIDs of any agent still running from this install dir. Matches the binary path in
# the process argv (which /proc keeps even after the binary file is deleted), so it
# still finds a process an earlier broken uninstall reparented to init. Portable:
# uses pgrep where present, else ps -ef - some NAS/embedded hosts ship neither
# pgrep nor pkill (procps), but ps -ef is universal.
agent_pids() {
    if command -v pgrep >/dev/null 2>&1; then
        pgrep -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
    else
        ps -ef 2>/dev/null | grep "${INSTALL_DIR}/NetworkOptimizer.Agent" | grep -v grep | awk '{print $2}' || true
    fi
}

agent_running() { [ -n "$(agent_pids)" ]; }

# Stop and reap the agent (and the iperf3 -s it spawns) regardless of the unit's
# state. 'systemctl disable --now' is NOT enough on its own: a prior partial
# uninstall can leave the unit 'Loaded: not-found' but still 'active (running)', and
# disable then aborts on the missing unit file and skips the '--now' stop, leaving
# the cgroup running. A bare stop/kill still reaps the loaded runtime unit (its
# cgroup is intact); a direct signal covers a process already orphaned to init.
stop_and_reap_agent() {
    systemctl stop "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    systemctl kill --signal=SIGKILL "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    systemctl disable "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    agent_running || return 0
    warn "Agent still running after systemctl stop; reaping the process directly"
    if command -v pkill >/dev/null 2>&1; then
        pkill -TERM -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
        for _ in 1 2 3 4 5; do agent_running || break; sleep 1; done
        pkill -KILL -f "${INSTALL_DIR}/NetworkOptimizer.Agent" 2>/dev/null || true
        # The iperf3 -s child can outlive a killed agent (reparented to init).
        pkill -KILL -f 'iperf3 -s -p 5201' 2>/dev/null || true
    else
        local pids; pids="$(agent_pids)"
        [ -n "$pids" ] && kill -TERM $pids 2>/dev/null || true
        for _ in 1 2 3 4 5; do agent_running || break; sleep 1; done
        pids="$(agent_pids)"; [ -n "$pids" ] && kill -KILL $pids 2>/dev/null || true
    fi
}

uninstall_agent() {
    step "Removing the Network Optimizer agent"
    note "${SERVICE_NAME} + ${INSTALL_DIR}"
    # Whether this install ever set up the speed test (and thus borrowed nginx) - so
    # the summary only mentions nginx/AppArmor when they were actually involved.
    local had_speedtest=0
    [ -f "/etc/systemd/system/${SPEEDTEST_SERVICE}.service" ] && had_speedtest=1
    stop_and_reap_agent
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service" \
          "/etc/systemd/system/${SPEEDTEST_SERVICE}.service" \
          "/etc/systemd/system/multi-user.target.wants/${SERVICE_NAME}.service"
    systemctl daemon-reload 2>/dev/null || true
    systemctl reset-failed "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    remove_apparmor_override
    rm -rf "$INSTALL_DIR"
    # Never report success while the agent is still alive - a survivor keeps serving
    # iperf3 and relaying speed-test results to the central server.
    if agent_running; then
        err "Agent process is STILL running after teardown (PID(s): $(agent_pids | tr '\n' ' ')). Kill it manually (e.g. kill -9 <pid>) and re-run --uninstall."
    fi
    if [ "${AA_OVERRIDE_REMOVED:-0}" = 1 ]; then
        ok "Agent removed, including the AppArmor override it had added. The host's own nginx is untouched."
    elif [ "$had_speedtest" = 1 ]; then
        ok "Agent removed. The host's own nginx is untouched."
    else
        ok "Agent removed."
    fi
    printf '\n'
}

[ "$(id -u)" -eq 0 ] || err "Run as root (needed to manage the systemd service): sudo bash install-native.sh ..."
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)"

# Teardown short-circuits the install: needs neither --server nor a token.
if [ "$UNINSTALL" = true ]; then
    uninstall_agent
    exit 0
fi

# This is the separate-box installer: it targets /opt, installs an unfenced
# service unit, and can host the LAN speed test - all wrong on a UniFi gateway,
# where the identical unit name would clobber the memory-fenced on-gateway
# install and re-enroll it as a new agent. ubnt-device-info ships on every
# UniFi OS gateway and nowhere else, so its presence is the refusal signal.
# (--uninstall above is deliberately NOT guarded: cleaning up a mistaken
# install on a gateway must always be possible.)
if [ "$FORCE_NATIVE" != true ] && command -v ubnt-device-info >/dev/null 2>&1; then
    err "This host is a UniFi OS gateway ($(ubnt-device-info model 2>/dev/null || echo model unknown)) - use the on-gateway installer instead:
  curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/release/2.8/scripts/agent/install-agent-gateway.sh | bash -s -- --server ... --token ...
Re-run with --force-native to override (not recommended: no memory fence, and the LAN speed test does not belong on the router)."
fi

[ -n "$SERVER" ] || err "--server is required (the central server's HTTPS address)"
case "$SERVER" in
    https://*) ;;
    *) err "--server must be an https:// URL (the agent refuses cleartext)" ;;
esac
command -v curl >/dev/null 2>&1 || err "curl is required"

# Map machine architecture to the published self-contained runtime identifier.
case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) err "Unsupported architecture: $(uname -m). Build from source (see the agent README)." ;;
esac

printf '\n%sNetwork Optimizer on-site agent%s\n' "$_b" "$_rst"
note "Installing to ${INSTALL_DIR}  (${RID})"
mkdir -p "$INSTALL_DIR"

# Binaries are downloaded to a temp name and renamed into place: writing over a
# binary while it is running fails with ETXTBSY, but rename swaps the directory
# entry and any running process keeps its old inode until the restart below.

step "Downloading binaries"
# Agent binary
curl -fsSL "${RELEASE_BASE}/NetworkOptimizer.Agent-${RID}" -o "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
chmod +x "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
mv -f "${INSTALL_DIR}/NetworkOptimizer.Agent.new" "${INSTALL_DIR}/NetworkOptimizer.Agent"
ok "agent (${RID})"

# uwnspeedtest binary for site-local WAN speed tests; the agent resolves it next
# to itself (AppContext.BaseDirectory/uwnspeedtest).
curl -fsSL "${RELEASE_BASE}/uwnspeedtest-${RID}" -o "${INSTALL_DIR}/uwnspeedtest.new"
chmod +x "${INSTALL_DIR}/uwnspeedtest.new"
mv -f "${INSTALL_DIR}/uwnspeedtest.new" "${INSTALL_DIR}/uwnspeedtest"
ok "WAN speed test helper"

CONFIG="${INSTALL_DIR}/agent.json"

# Whether this host is a UniFi OS gateway, for agent.json's onGateway key. The refusal
# above means this normally answers false; under --force-native on a real gateway,
# ubnt-device-info is present and the truth wins over the override.
ON_GATEWAY=false
command -v ubnt-device-info >/dev/null 2>&1 && ON_GATEWAY=true

step "Configuring the agent"
# Preserve an already-enrolled config so re-running the installer (e.g. to
# update the binary) never wipes the persisted agent key.
if grep -q '"agentKey"' "$CONFIG" 2>/dev/null; then
    note "Existing enrollment found - keeping agent.json"
    # Upgrades keep the enrolled config, so the on-gateway key is ADDED when missing (#1108).
    if ! grep -q '"onGateway"' "$CONFIG"; then
        sed -i "0,/{/s/{/{\n  \"onGateway\": ${ON_GATEWAY},/" "$CONFIG"
    fi
    # ...except the speed test port, when this run asked for a different one. nginx's listener is
    # rewritten below either way, and the agent announces the port from here - so leaving this
    # alone would have the agent advertising a port it no longer serves, which is precisely the
    # mismatch the announcement exists to prevent.
    if [ "$PORT_REQUESTED" = true ]; then
        if grep -q '"lanSpeedTestPort"' "$CONFIG"; then
            sed -i "s/\"lanSpeedTestPort\": *[0-9]*/\"lanSpeedTestPort\": ${SPEED_TEST_PORT}/" "$CONFIG"
        else
            sed -i "s/\"lanSpeedTest\": true/\"lanSpeedTest\": true,\n  \"lanSpeedTestPort\": ${SPEED_TEST_PORT}/" "$CONFIG"
        fi
        note "Speed test port set to ${SPEED_TEST_PORT} in agent.json"
    fi
else
    [ -n "$TOKEN" ] || err "--token is required for a first-time install"
    {
        echo "{"
        echo "  \"serverUrl\": \"${SERVER%/}\","
        echo "  \"tunnelUrl\": \"${SERVER%/}\","
        echo "  \"enrollmentToken\": \"${TOKEN}\","
        echo "  \"onGateway\": ${ON_GATEWAY},"
        printf '  "ignoreSslErrors": %s' "$INSECURE"
        if [ "$LAN_SPEED_TEST" = true ]; then
            printf ',\n  "lanSpeedTest": true'
            # The agent announces this to the server, so in-app speed test links follow it.
            [ "$SPEED_TEST_PORT" != "24443" ] && printf ',
  "lanSpeedTestPort": %s' "$SPEED_TEST_PORT"
        fi
        printf '\n}\n'
    } > "$CONFIG"
    ok "Wrote ${CONFIG}"
fi

# nginx serves the OpenSpeedTest page + the throughput-critical transfer legs
# (sendfile, 10 GbE); the agent's loopback relay (127.0.0.1:24042) forwards result
# posts to the central server. Only needed with --lan-speed-test.
#
# We run our OWN dedicated nginx master - its own config, webroot, pidfile, and
# systemd unit (netopt-speedtest-nginx) - and NEVER touch any system nginx the host
# may already run (a reverse proxy, a NAS appliance's web UI, etc.). Dropping a
# conf.d file and running `systemctl restart nginx` would hijack or bounce that
# unrelated instance, which is unacceptable. We only borrow the nginx *binary*.
if [ "$LAN_SPEED_TEST" = true ]; then
    step "Setting up the LAN speed test (nginx)"
    if ! command -v nginx >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq nginx
        elif command -v dnf >/dev/null 2>&1; then dnf install -y -q nginx
        elif command -v yum >/dev/null 2>&1; then yum install -y -q nginx
        elif command -v apk >/dev/null 2>&1; then apk add --no-cache nginx
        else warn "could not install nginx automatically - install it and re-run to enable the LAN speed test."; fi
    fi

    NGINX_BIN="$(command -v nginx 2>/dev/null || echo /usr/sbin/nginx)"
    if [ -x "$NGINX_BIN" ]; then
        # Webroot lives beside the deployables, not in /usr/share/nginx/html (which
        # may belong to the system nginx or sit on a read-only root on appliances).
        WEBROOT="${INSTALL_DIR}/speedtest-web"
        # PREVIEW-CYCLE PIN (2.8.0) - REVERT BEFORE STABLE: assets from release/2.8, not main.
        RAW="https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/release/2.8"
        mkdir -p "$WEBROOT/assets/js"
        note "Fetching the OpenSpeedTest page"
        TARBALL="$(mktemp)"; TMPX="$(mktemp -d)"
        curl -fsSL "https://github.com/Ozark-Connect/NetworkOptimizer/archive/refs/heads/release/2.8.tar.gz" -o "$TARBALL"
        tar -xzf "$TARBALL" -C "$TMPX" --strip-components=3 "NetworkOptimizer-release-2.8/src/OpenSpeedTest"
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
        # World-readable so nginx's unprivileged workers can serve it wherever the
        # install dir lives (e.g. under /root on some appliances).
        chmod -R a+rX "$WEBROOT"

        # Our server block + standalone wrapper - the exact same two files the Docker
        # image uses, so the nginx config has a single source. Webroot repointed to the
        # install dir; the wrapper's placeholder paths filled in for this install so it
        # runs as an independent master rather than a system-nginx drop-in.
        curl -fsSL "$RAW/docker/agent/nginx.conf" -o "${INSTALL_DIR}/nginx-speedtest-server.conf"
        sed -i "s#root /usr/share/nginx/html;#root ${WEBROOT};#" "${INSTALL_DIR}/nginx-speedtest-server.conf"

        # Serve on a different port when asked. Only the public listener moves; the loopback
        # relay stays where the agent binary expects it. The agent announces the port it was
        # given, so the server's links follow without being told separately.
        if [ "$SPEED_TEST_PORT" != "24443" ]; then
            sed -i "/listen/ s/24443/${SPEED_TEST_PORT}/" "${INSTALL_DIR}/nginx-speedtest-server.conf"
            note "LAN speed test page on port ${SPEED_TEST_PORT}"
        fi

        if [ "${AGENT_SPEEDTEST_TLS:-1}" = "0" ]; then
            # Self-signed TLS opt-out (AGENT_SPEEDTEST_TLS=0 at install time): serve the
            # speed test listener as plain http - for sites already behind their own
            # reverse proxy / TLS, or shaving TLS overhead on high-throughput LANs. Strips
            # ssl from the listener, drops the ssl_* directives, skips cert generation.
            # The app side must then reach this agent via an http:// per-site speed-test
            # URL override (the app defaults to https).
            sed -i \
                -e 's/^\([[:space:]]*listen[[:space:]][^;]*\) ssl\([^;]*;\)/\1\2/' \
                -e '/^[[:space:]]*ssl_/d' \
                "${INSTALL_DIR}/nginx-speedtest-server.conf"
            note "AGENT_SPEEDTEST_TLS=0 - serving plain http on port ${SPEED_TEST_PORT}"
        else
            # Persisted self-signed cert for the LAN speed test's TLS listener (secure context
            # for the browser Geolocation API / GPS-tagged results, no per-site reverse proxy).
            # SANs cover the host's LAN IPs + hostname; persisted so a client's browser trust
            # exception survives restarts. Wire the cert paths into the server block.
            CERTDIR="${INSTALL_DIR}/speedtest-tls"
            mkdir -p "$CERTDIR"
            if [ ! -f "$CERTDIR/cert.pem" ] && command -v openssl >/dev/null 2>&1; then
                IPS=$(hostname -I 2>/dev/null || echo)
                SAN="DNS:$(hostname),DNS:localhost"
                for ip in $IPS; do SAN="$SAN,IP:$ip"; done
                CN=$(echo "$IPS" | awk '{print $1}')
                openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
                    -keyout "$CERTDIR/key.pem" -out "$CERTDIR/cert.pem" \
                    -subj "/CN=${CN:-agent}" -addext "subjectAltName=$SAN" >/dev/null 2>&1 \
                    && chmod 600 "$CERTDIR/key.pem" \
                    || warn "self-signed cert generation failed - the LAN speed test won't serve over TLS."
            fi
            sed -i \
                -e "s#__CERTFILE__#${CERTDIR}/cert.pem#" \
                -e "s#__KEYFILE__#${CERTDIR}/key.pem#" \
                "${INSTALL_DIR}/nginx-speedtest-server.conf"
        fi

        curl -fsSL "$RAW/docker/agent/nginx-standalone.conf" -o "${INSTALL_DIR}/nginx-speedtest.conf"
        sed -i \
            -e "s#__PIDFILE__#${INSTALL_DIR}/nginx.pid#" \
            -e "s#__ERRORLOG__#${INSTALL_DIR}/nginx-error.log#" \
            -e "s#__SERVERCONF__#${INSTALL_DIR}/nginx-speedtest-server.conf#" \
            "${INSTALL_DIR}/nginx-speedtest.conf"

        # If the install dir isn't world-traversable (e.g. under /root, mode 700),
        # unprivileged nginx workers can't reach the webroot and every request 403s.
        # Run the workers as root in that case so the page serves wherever it lives;
        # a world-traversable dir (the /opt default) keeps the safer unprivileged user.
        if _webroot_needs_root_worker "$WEBROOT"; then
            sed -i "1i user root;" "${INSTALL_DIR}/nginx-speedtest.conf"
            note "${INSTALL_DIR} isn't world-traversable, so the speed test nginx runs its workers as root"
        fi

        # Dedicated systemd unit for OUR nginx master - separate from the system one.
        cat > /etc/systemd/system/netopt-speedtest-nginx.service <<UNIT
[Unit]
Description=Network Optimizer LAN speed test (nginx)
# The speed test only matters when the agent is up (results relay through the
# agent on 24042), so bind nginx's lifecycle to the agent: it starts after the
# agent and stops whenever the agent stops or crashes. Mirrors the Docker
# single-container model where the agent is PID 1 and nginx dies with it.
After=network-online.target netopt-agent.service
Wants=network-online.target
BindsTo=netopt-agent.service

[Service]
Type=forking
PIDFile=${INSTALL_DIR}/nginx.pid
ExecStartPre=${NGINX_BIN} -t -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecStart=${NGINX_BIN} -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecReload=${NGINX_BIN} -s reload -c ${INSTALL_DIR}/nginx-speedtest.conf
ExecStop=${NGINX_BIN} -s quit -c ${INSTALL_DIR}/nginx-speedtest.conf
Restart=always
RestartSec=5

# Enabling under the AGENT's .wants (not multi-user's): BindsTo above stops
# nginx with the agent but never starts it again, so hook the start side to the
# agent too - every agent start (boot, deploys, manual restarts, the async-I/O
# watchdog's self-restart) relights nginx. Lives here in the nginx unit so the
# dependency only exists on installs that actually include the speed test.
[Install]
WantedBy=netopt-agent.service
UNIT

        # If the first test fails only because an enforcing AppArmor profile denies
        # our install dir, add a local override and let the re-test below decide. This
        # is skipped entirely when the test already passes, so working hosts are
        # untouched.
        if ! "$NGINX_BIN" -t -c "${INSTALL_DIR}/nginx-speedtest.conf" >/dev/null 2>&1; then
            maybe_fix_apparmor_nginx
        fi

        if "$NGINX_BIN" -t -c "${INSTALL_DIR}/nginx-speedtest.conf" >/dev/null 2>&1; then
            systemctl daemon-reload
            # Enable now, but START it below, after the agent unit is installed and
            # running - nginx BindsTo the agent, so starting it before the agent exists
            # would immediately stop it again.
            # reenable (not enable): converges upgrade re-runs onto the current
            # WantedBy - enable alone would leave a stale multi-user.target.wants
            # symlink from installs made before nginx was wanted by the agent.
            systemctl reenable --quiet netopt-speedtest-nginx.service
            START_SPEEDTEST_NGINX=1
            ok "LAN speed test ready on port ${SPEED_TEST_PORT} (starts with the agent)"
        else
            warn "nginx config test failed - the LAN speed test page won't serve."
            print_speedtest_apparmor_hint
            note "Diagnose: sudo $NGINX_BIN -t -c ${INSTALL_DIR}/nginx-speedtest.conf"
        fi
    fi
fi

# systemd unit
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

# nginx is bound to the agent (BindsTo), so it was stopped by the agent restart
# (or was never started - starting it before the agent exists would immediately
# stop it again). Start it whenever its unit is enabled: fresh install with the
# speed test, or an upgrade re-run on a box that already had it.
if [ "${START_SPEEDTEST_NGINX:-0}" = 1 ] || systemctl is-enabled --quiet netopt-speedtest-nginx.service 2>/dev/null; then
    systemctl start netopt-speedtest-nginx.service
fi

step "Done"
ok "Agent installed and running"
note "It enrolls, then holds a tunnel to ${SERVER%/} - watch it come Online in the web UI."
note "Logs: journalctl -u ${SERVICE_NAME} -f"
printf '\n'
