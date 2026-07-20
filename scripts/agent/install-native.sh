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
#   --uninstall      Stop and remove the agent, its services, install dir, and any
#                    AppArmor override this installer added, then exit

set -euo pipefail

SERVER=""
TOKEN=""
LAN_SPEED_TEST=false
INSECURE=false
UNINSTALL=false
INSTALL_DIR="/opt/netopt-agent"
SERVICE_NAME="netopt-agent"
SPEEDTEST_SERVICE="netopt-speedtest-nginx"
RELEASE_BASE="https://github.com/Ozark-Connect/NetworkOptimizer/releases/latest/download"

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

err() { echo "Error: $*" >&2; exit 1; }

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
# dir. Only attempted when the vendor profile is a file under /etc/apparmor.d (the
# only place a local/ drop-in is consumed). Never edits the vendor profile; reloads
# only that one profile. A parse error makes apparmor_parser abort and keep the
# running profile, so a bad override can't unconfine the host's own nginx. Caller
# re-tests `nginx -t` to judge the result.
maybe_fix_apparmor_nginx() {
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
    echo "  Cause: AppArmor profile '${pname}' is blocking ${INSTALL_DIR}, and a scoped"
    echo "  local override didn't clear it (no findable profile source / local hook)."
    echo "  To serve the speed test, relax the nginx profile, then start the service:"
    if command -v aa-complain >/dev/null 2>&1; then
        # Cleaner (complain mode: keeps the profile, just logs) - when apparmor-utils
        # is present. Needs a profile source file, which appliance distros may lack.
        echo "    sudo aa-complain '${pname}'"
    else
        # Tool-free and source-free: works by profile name on any confined box.
        echo "    echo -n '${pname}' | sudo tee /sys/kernel/security/apparmor/.remove   # unload, until reboot"
    fi
    echo "    sudo systemctl start ${SPEEDTEST_SERVICE}"
    echo "  The agent and monitoring work regardless - only the LAN speed test needs this."
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
        echo "  Removed AppArmor override from ${f}."
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
uninstall_agent() {
    echo "Removing the Network Optimizer agent (${SERVICE_NAME}) and ${INSTALL_DIR}..."
    # Whether this install ever set up the speed test (and thus borrowed nginx) - so
    # the summary only mentions nginx/AppArmor when they were actually involved.
    local had_speedtest=0
    [ -f "/etc/systemd/system/${SPEEDTEST_SERVICE}.service" ] && had_speedtest=1
    systemctl disable --now "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service" \
          "/etc/systemd/system/${SPEEDTEST_SERVICE}.service" \
          "/etc/systemd/system/multi-user.target.wants/${SERVICE_NAME}.service"
    systemctl daemon-reload 2>/dev/null || true
    systemctl reset-failed "${SERVICE_NAME}.service" "${SPEEDTEST_SERVICE}.service" 2>/dev/null || true
    remove_apparmor_override
    rm -rf "$INSTALL_DIR"
    if [ "${AA_OVERRIDE_REMOVED:-0}" = 1 ]; then
        echo "Done - agent removed, including the AppArmor override it had added. The host's own nginx is untouched."
    elif [ "$had_speedtest" = 1 ]; then
        echo "Done - agent removed. The host's own nginx is untouched."
    else
        echo "Done - agent removed."
    fi
}

[ "$(id -u)" -eq 0 ] || err "Run as root (needed to manage the systemd service): sudo bash install-native.sh ..."
command -v systemctl >/dev/null 2>&1 || err "systemd is required (systemctl not found)"

# Teardown short-circuits the install: needs neither --server nor a token.
if [ "$UNINSTALL" = true ]; then
    uninstall_agent
    exit 0
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

echo "Installing Network Optimizer agent to ${INSTALL_DIR} (${RID})"
mkdir -p "$INSTALL_DIR"

# Binaries are downloaded to a temp name and renamed into place: writing over a
# binary while it is running fails with ETXTBSY, but rename swaps the directory
# entry and any running process keeps its old inode until the restart below.

# Agent binary
echo "Downloading agent binary..."
curl -fSL "${RELEASE_BASE}/NetworkOptimizer.Agent-${RID}" -o "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
chmod +x "${INSTALL_DIR}/NetworkOptimizer.Agent.new"
mv -f "${INSTALL_DIR}/NetworkOptimizer.Agent.new" "${INSTALL_DIR}/NetworkOptimizer.Agent"

# uwnspeedtest binary for site-local WAN speed tests; the agent resolves it next
# to itself (AppContext.BaseDirectory/uwnspeedtest).
echo "Downloading WAN speed test binary..."
curl -fSL "${RELEASE_BASE}/uwnspeedtest-${RID}" -o "${INSTALL_DIR}/uwnspeedtest.new"
chmod +x "${INSTALL_DIR}/uwnspeedtest.new"
mv -f "${INSTALL_DIR}/uwnspeedtest.new" "${INSTALL_DIR}/uwnspeedtest"

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
#
# We run our OWN dedicated nginx master - its own config, webroot, pidfile, and
# systemd unit (netopt-speedtest-nginx) - and NEVER touch any system nginx the host
# may already run (a reverse proxy, a NAS appliance's web UI, etc.). Dropping a
# conf.d file and running `systemctl restart nginx` would hijack or bounce that
# unrelated instance, which is unacceptable. We only borrow the nginx *binary*.
if [ "$LAN_SPEED_TEST" = true ]; then
    echo "Setting up a dedicated nginx instance for the LAN speed test..."
    if ! command -v nginx >/dev/null 2>&1; then
        if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -qq nginx
        elif command -v dnf >/dev/null 2>&1; then dnf install -y -q nginx
        elif command -v yum >/dev/null 2>&1; then yum install -y -q nginx
        elif command -v apk >/dev/null 2>&1; then apk add --no-cache nginx
        else echo "WARNING: could not install nginx automatically - install it and re-run to enable the LAN speed test."; fi
    fi

    NGINX_BIN="$(command -v nginx 2>/dev/null || echo /usr/sbin/nginx)"
    if [ -x "$NGINX_BIN" ]; then
        # Webroot lives beside the deployables, not in /usr/share/nginx/html (which
        # may belong to the system nginx or sit on a read-only root on appliances).
        WEBROOT="${INSTALL_DIR}/speedtest-web"
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
        # World-readable so nginx's unprivileged workers can serve it wherever the
        # install dir lives (e.g. under /root on some appliances).
        chmod -R a+rX "$WEBROOT"

        # Our server block + standalone wrapper - the exact same two files the Docker
        # image uses, so the nginx config has a single source. Webroot repointed to the
        # install dir; the wrapper's placeholder paths filled in for this install so it
        # runs as an independent master rather than a system-nginx drop-in.
        curl -fsSL "$RAW/docker/agent/nginx.conf" -o "${INSTALL_DIR}/nginx-speedtest-server.conf"
        sed -i "s#root /usr/share/nginx/html;#root ${WEBROOT};#" "${INSTALL_DIR}/nginx-speedtest-server.conf"

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
            echo "AGENT_SPEEDTEST_TLS=0 - LAN speed test will serve plain http on port 3000."
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
                    || echo "WARNING: self-signed cert generation failed - the LAN speed test won't serve over TLS."
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
            echo "Note: ${INSTALL_DIR} isn't world-traversable, so the speed test nginx runs its workers as root."
        fi

        # Dedicated systemd unit for OUR nginx master - separate from the system one.
        cat > /etc/systemd/system/netopt-speedtest-nginx.service <<UNIT
[Unit]
Description=Network Optimizer LAN speed test (nginx)
# The speed test only matters when the agent is up (results relay through the
# agent on 3001), so bind nginx's lifecycle to the agent: it starts after the
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

[Install]
WantedBy=multi-user.target
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
            systemctl enable netopt-speedtest-nginx.service
            START_SPEEDTEST_NGINX=1
            echo "Dedicated nginx for OpenSpeedTest on port 3000 will start with the agent (netopt-speedtest-nginx.service)."
        else
            echo "WARNING: nginx config test failed - the LAN speed test page won't serve."
            print_speedtest_apparmor_hint
            echo "Diagnose with: $NGINX_BIN -t -c ${INSTALL_DIR}/nginx-speedtest.conf"
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
systemctl enable "${SERVICE_NAME}.service"
# restart (not `enable --now`) so an upgrade re-run moves an already-running
# agent onto the new binary; it starts a stopped/fresh service just the same
systemctl restart "${SERVICE_NAME}.service"

# nginx is bound to the agent (BindsTo), so it was stopped by the agent restart
# (or was never started - starting it before the agent exists would immediately
# stop it again). Start it whenever its unit is enabled: fresh install with the
# speed test, or an upgrade re-run on a box that already had it.
if [ "${START_SPEEDTEST_NGINX:-0}" = 1 ] || systemctl is-enabled --quiet netopt-speedtest-nginx.service 2>/dev/null; then
    systemctl start netopt-speedtest-nginx.service
fi

echo
echo "Agent started. It enrolls, then holds a tunnel to ${SERVER%/}."
echo "Watch it come Online in the web UI, or follow logs:"
echo "  journalctl -u ${SERVICE_NAME} -f"
