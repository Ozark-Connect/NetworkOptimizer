#!/bin/sh
# The agent container runs two processes: nginx (serves OpenSpeedTest + the
# throughput-critical transfer legs on port 24443) and the .NET agent (the tunnel
# plus the loopback results relay on 24042). nginx runs in the background; the
# agent is the main process, so `docker stop` (SIGTERM to it) tears the container
# down and nginx goes with it.
set -e

# Which port the page is served on. nginx and the agent must resolve this identically or the agent
# announces a port nginx is not listening on: environment variable first, then whatever agent.json
# records, then the image's own default. The config case matters on upgrade - an agent enrolled
# before the port was configurable has 3000 written in its config and should keep serving it, rather
# than being moved to a port its clients and firewall rules have never heard of. Deleting that line
# from agent.json is all it takes to adopt the current default.
# Start from the pristine config every boot: a restart reuses the container filesystem, so
# editing the live file in place would make the first boot's choices permanent.
cp /etc/nginx/netopt-speedtest-server.conf.template /etc/nginx/netopt-speedtest-server.conf

PAGE_PORT="${AGENT_SPEEDTEST_PORT:-}"
if [ -z "$PAGE_PORT" ] && [ -f /data/agent.json ]; then
    PAGE_PORT=$(sed -n 's/.*"lanSpeedTestPort"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' /data/agent.json | head -1)
fi
if [ -n "$PAGE_PORT" ] && [ "$PAGE_PORT" != "24443" ]; then
    sed -i "/listen/ s/24443/${PAGE_PORT}/" /etc/nginx/netopt-speedtest-server.conf
    echo "entrypoint: LAN speed test page on port ${PAGE_PORT}"
fi

# Plain HTTP twin listener the HTTPS page hands the test to (AGENT_SPEEDTEST_HTTP_PORT, default
# 24080; 0 removes it and turns the handoff off). config.js is rewritten in place by pattern, so
# repeated boots converge on the current setting.
HTTP_PORT="${AGENT_SPEEDTEST_HTTP_PORT:-24080}"
case "$HTTP_PORT" in
    *[!0-9]*|'') echo "entrypoint: invalid AGENT_SPEEDTEST_HTTP_PORT '$HTTP_PORT', using 24080"; HTTP_PORT=24080 ;;
esac
# A port something else holds would stop nginx from starting at all, taking the 24443 page with it,
# so a taken port turns the handoff off instead. /proc, because the image ships no ss.
port_in_use() {
    awk -v h=":$(printf '%04X' "$1")" '$4 == "0A" && toupper($2) ~ (h "$") { f = 1 } END { exit !f }' \
        /proc/net/tcp /proc/net/tcp6 2>/dev/null
}
if [ "$HTTP_PORT" != "0" ] && port_in_use "$HTTP_PORT"; then
    echo "entrypoint: port ${HTTP_PORT} is in use on this host - plain HTTP handoff off, the test stays on HTTPS"
    HTTP_PORT=0
fi
case "$HTTP_PORT" in
    0) sed -i '/listen.*24080/d' /etc/nginx/netopt-speedtest-server.conf
       echo "entrypoint: plain HTTP handoff off" ;;
    24080) ;;
    *) sed -i "/listen/ s/24080/${HTTP_PORT}/" /etc/nginx/netopt-speedtest-server.conf
       echo "entrypoint: plain HTTP handoff on port ${HTTP_PORT}" ;;
esac
sed -i "s/^var plainHttpPort = .*/var plainHttpPort = ${HTTP_PORT};/" /usr/share/nginx/html/assets/js/config.js

# Self-signed TLS opt-out (AGENT_SPEEDTEST_TLS=0): serve the speed test listener as
# plain http instead - for sites already behind their own reverse proxy / TLS, or
# shaving TLS overhead on high-throughput LANs. Strips ssl from the listener and
# drops the ssl_* directives; cert generation is skipped. The app side must then
# reach this agent via an http:// per-site speed-test URL override, since the app
# defaults to https. Anything other than "0" (including unset) keeps today's TLS.
if [ "${AGENT_SPEEDTEST_TLS:-1}" = "0" ]; then
    sed -i \
        -e 's/^\([[:space:]]*listen[[:space:]][^;]*\) ssl\([^;]*;\)/\1\2/' \
        -e '/^[[:space:]]*ssl_/d' \
        /etc/nginx/netopt-speedtest-server.conf
    echo "entrypoint: AGENT_SPEEDTEST_TLS=0 - LAN speed test serving plain http on 24443"
else
    # Generate a persisted self-signed cert for the LAN speed test's TLS listener if it
    # doesn't exist yet. TLS makes the OpenSpeedTest page a secure context so the browser
    # Geolocation API works (GPS-tagged results) without a per-site reverse proxy. Persisted
    # under /data so a client's one-time browser trust exception survives restarts; SANs
    # cover the host's LAN IPs + hostname so the cert matches the address clients use.
    # Non-fatal: if generation fails, nginx just won't serve (the agent still runs).
    CERTDIR=/data/speedtest-tls
    if [ ! -f "$CERTDIR/cert.pem" ]; then
        mkdir -p "$CERTDIR"
        IPS=$(hostname -I 2>/dev/null || echo)
        SAN="DNS:$(hostname),DNS:localhost"
        for ip in $IPS; do SAN="$SAN,IP:$ip"; done
        CN=$(echo "$IPS" | awk '{print $1}')
        openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
            -keyout "$CERTDIR/key.pem" -out "$CERTDIR/cert.pem" \
            -subj "/CN=${CN:-agent}" -addext "subjectAltName=$SAN" >/dev/null 2>&1 \
            && chmod 600 "$CERTDIR/key.pem" \
            || echo "entrypoint: self-signed cert generation failed; LAN speed test TLS unavailable"
    fi
fi

nginx -c /etc/nginx/netopt-speedtest.conf -g 'daemon off;' &
exec /app/NetworkOptimizer.Agent
