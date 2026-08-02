#!/bin/sh
# The agent container runs two processes: nginx (serves OpenSpeedTest + the
# throughput-critical transfer legs on port 24443) and the .NET agent (the tunnel
# plus the loopback results relay on 24042). nginx runs in the background; the
# agent is the main process, so `docker stop` (SIGTERM to it) tears the container
# down and nginx goes with it.
set -e

# Serve the page somewhere other than 24443 when asked. Only the public listener moves - the
# loopback relay stays where the agent binary expects it - and the agent announces the port it
# was given, so the server's speed test links follow without being configured separately.
if [ -n "${AGENT_SPEEDTEST_PORT:-}" ] && [ "${AGENT_SPEEDTEST_PORT}" != "24443" ]; then
    sed -i "/listen/ s/24443/${AGENT_SPEEDTEST_PORT}/" /etc/nginx/netopt-speedtest-server.conf
    echo "entrypoint: LAN speed test page on port ${AGENT_SPEEDTEST_PORT}"
fi

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
