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

# --- LAN speed test nginx: AppArmor remediation -------------------------------
# Some distros ship an ENFORCING AppArmor profile on the nginx binary that only
# permits nginx's stock paths (/var/log/nginx, /run/nginx.pid, ...). Our dedicated
# speed test master keeps its pid, logs, and webroot under the install dir, which
# such a profile denies even to root - the failure is a MAC denial, not file
# permissions. Everything below runs ONLY after `nginx -t` has already failed and
# only acts on a real AppArmor denial of our install dir, so any host where the
# speed test already works never enters this path.

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
    for dir in /etc/apparmor.d /var/lib/snapd/apparmor/profiles; do
        [ -d "$dir" ] || continue
        if [ -f "$dir/$fname" ]; then
            cand="$dir/$fname"
        else
            cand="$(grep -rslF "$pname" "$dir" 2>/dev/null | grep -v '/local/' | head -1 || true)"
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
        /etc/apparmor.d/*) ;;
        # Not file-backed under /etc/apparmor.d (non-standard or snap profile): a
        # local/ override wouldn't be consumed. Leave it to the hint below.
        *) return 0 ;;
    esac
    base="$(basename "$file")"

    echo "AppArmor is blocking ${INSTALL_DIR}; adding a local override (/etc/apparmor.d/local/${base}) and reloading."
    mkdir -p /etc/apparmor.d/local
    cat > "/etc/apparmor.d/local/${base}" <<AAOVERRIDE
# Added by the Network Optimizer agent installer.
# Lets the dedicated LAN speed test nginx master use its pid, logs, and webroot
# under the install dir. Additive and local-only; delete this file to revert.
${INSTALL_DIR}/ r,
${INSTALL_DIR}/** rw,
AAOVERRIDE

    apparmor_parser -r "$file" >/dev/null 2>&1 \
        || echo "  apparmor_parser reload failed; the override may not have taken (the profile may not include local/${base})."
    return 0
}

# Actionable manual remediation, printed only when the speed test nginx still fails
# because of an AppArmor denial (so it never nags on unrelated failures). Names the
# real profile and picks the applicable fix: a local override when the profile is a
# standard /etc/apparmor.d file, otherwise complain mode.
print_speedtest_apparmor_hint() {
    _aa_denied_install_dir || return 0
    local pname file base
    pname="$(_aa_nginx_profile_name)"; [ -n "$pname" ] || pname="$NGINX_BIN"
    file="$(_aa_nginx_profile_file)"
    echo "  Cause: AppArmor profile '${pname}' is blocking ${INSTALL_DIR}."
    case "$file" in
        /etc/apparmor.d/*)
            base="$(basename "$file")"
            echo "  Fix - add a local override, then reload the profile:"
            echo "    printf '%s\n%s\n' '${INSTALL_DIR}/ r,' '${INSTALL_DIR}/** rw,' | sudo tee /etc/apparmor.d/local/${base}"
            echo "    sudo apparmor_parser -r ${file}"
            echo "  Or relax it (logs but allows): sudo aa-complain '${pname}'"
            ;;
        *)
            echo "  Its profile isn't a standard file under /etc/apparmor.d, so relax it (logs but allows):"
            echo "    sudo aa-complain '${pname}'"
            ;;
    esac
    echo "  Then: sudo systemctl start netopt-speedtest-nginx"
}

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
