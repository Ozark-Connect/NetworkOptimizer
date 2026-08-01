#!/bin/sh
# Ozark Connect Speed Test - Entrypoint
# Injects runtime configuration into config.js

# Report TCP congestion control state. We can't set it from inside the container
# (/proc/sys is mounted read-only except for sysctls explicitly declared in the
# compose sysctls: block, and we can't put tcp_congestion_control there because it
# hard-fails container start on kernels without bbr — Synology, QNAP, some Proxmox
# setups). The container inherits whatever the host's default CC is, so we just
# surface the state and point users at the fix if bbr is available but not active.
CC_FILE="/proc/sys/net/ipv4/tcp_congestion_control"
AVAIL_FILE="/proc/sys/net/ipv4/tcp_available_congestion_control"
if [ -r "$CC_FILE" ]; then
    CURRENT_CC=$(cat "$CC_FILE")
    AVAIL_CC=$(cat "$AVAIL_FILE" 2>/dev/null || echo "unknown")
    echo "TCP congestion control: $CURRENT_CC (available: $AVAIL_CC)"
    case " $AVAIL_CC " in
        *" bbr "*)
            if [ "$CURRENT_CC" != "bbr" ]; then
                echo "NOTE: bbr is loaded on the host but not the default. For best speedtest accuracy on shallow-policer WAN paths, set it as default on the host: sysctl -w net.ipv4.tcp_congestion_control=bbr"
            fi
            ;;
        *)
            echo "NOTE: bbr kernel module is not loaded on the host. For best speedtest accuracy on shallow-policer WAN paths, load it on the host: modprobe tcp_bbr (and persist via /etc/modules-load.d/bbr.conf)"
            ;;
    esac
fi

# API endpoint path (single source of truth)
API_PATH="/api/public/speedtest/results"

# Fold REVERSE_PROXIED_PORT into the hostname once, so everything below treats the result as a
# single authority. 443 stays implicit (an origin that spells out :443 does not match the browser's
# own), and a port already written into the hostname wins over the separate setting.
PROXIED_AUTHORITY="$REVERSE_PROXIED_HOST_NAME"
PROXIED_HOST="${REVERSE_PROXIED_HOST_NAME%%:*}"
if [ -n "$PROXIED_AUTHORITY" ] && [ -n "$REVERSE_PROXIED_PORT" ] && [ "$REVERSE_PROXIED_PORT" != "443" ]; then
    case "$PROXIED_AUTHORITY" in
        *:*) ;;
        *) PROXIED_AUTHORITY="${PROXIED_AUTHORITY}:${REVERSE_PROXIED_PORT}" ;;
    esac
fi

# Construct the save URL from environment variables
# Priority: REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
# IMPORTANT: Keep this logic in sync with NginxHostedService.cs:ConstructSaveDataUrl() (Windows installer)
if [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - https, and the proxy's own front-end port when it is not 443
    SAVE_DATA_URL="https://${PROXIED_AUTHORITY}${API_PATH}"
elif [ -n "$HOST_NAME" ]; then
    SAVE_DATA_URL="http://${HOST_NAME}:8042${API_PATH}"
elif [ -n "$HOST_IP" ]; then
    SAVE_DATA_URL="http://${HOST_IP}:8042${API_PATH}"
else
    # No explicit host configured - use dynamic URL (constructed client-side from browser location)
    SAVE_DATA_URL="__DYNAMIC__"
fi

# Inject configuration into config.js
CONFIG_FILE="/usr/share/nginx/html/assets/js/config.js"

if [ -f "$CONFIG_FILE" ]; then
    echo "Configuring speed test..."

    # saveData is always enabled - URL is either explicit or dynamic
    SAVE_DATA_VALUE="true"
    if [ "$SAVE_DATA_URL" = "__DYNAMIC__" ]; then
        echo "Results will be sent to: (dynamic - based on browser location):8042"
    else
        echo "Results will be sent to: $SAVE_DATA_URL"
    fi

    # External server ID (set for WAN speed test servers, empty for LAN)
    EXTERNAL_ID="${EXTERNAL_SERVER_ID:-}"

    # Replace placeholders with actual values
    sed -i "s|__SAVE_DATA__|$SAVE_DATA_VALUE|g" "$CONFIG_FILE"
    sed -i "s|__SAVE_DATA_URL__|$SAVE_DATA_URL|g" "$CONFIG_FILE"
    sed -i "s|__API_PATH__|$API_PATH|g" "$CONFIG_FILE"
    sed -i "s|__EXTERNAL_SERVER_ID__|$EXTERNAL_ID|g" "$CONFIG_FILE"

    if [ -n "$EXTERNAL_ID" ]; then
        echo "External server ID: $EXTERNAL_ID (WAN speed test mode)"
    fi

    echo "Configuration complete"
else
    echo "Warning: config.js not found at $CONFIG_FILE"
fi

# Strip IPv6 listen directive if IPv6 is not available on the host.
# Kernels with net.ipv6.conf.all.disable_ipv6=1 don't create /proc/net/if_inet6,
# and nginx will refuse to start if it can't bind [::].
NGINX_CONF="/etc/nginx/conf.d/default.conf"
if [ ! -f /proc/net/if_inet6 ]; then
    sed -i '/listen \[::\]/d' "$NGINX_CONF"
    echo "IPv6 not available on host - binding IPv4 only"
else
    echo "IPv6 available - binding dual-stack"
fi

# Enforce canonical URL via 302 redirect (matches UI logic exactly)
# Prevents browser caching issues on mobile
OST_PORT="${OPENSPEEDTEST_PORT:-3005}"
OST_HTTPS_PORT="${OPENSPEEDTEST_HTTPS_PORT:-443}"

# Match UI: OPENSPEEDTEST_HOST defaults to HOST_NAME, then - only when OPENSPEEDTEST_HTTPS=true -
# to the reverse-proxied host. That last rung is what an install serving the app and the speed test
# on one hostname (different ports) has set; without it there is no canonical URL, so the
# HTTP->HTTPS redirect below is never installed. It is gated on HTTPS because only then has the
# operator declared the speed test is served through the proxy on that hostname - ungated, it would
# newly install a host redirect on installs whose speed test is reached directly by LAN address.
# The bare host, not the authority: the port here is the speed test's, not the app's.
if [ "$OPENSPEEDTEST_HTTPS" = "true" ]; then
    OST_HOST="${OPENSPEEDTEST_HOST:-${HOST_NAME:-$PROXIED_HOST}}"
else
    OST_HOST="${OPENSPEEDTEST_HOST:-$HOST_NAME}"
fi

# Build canonical URL (same logic as ClientSpeedTest.razor)
# "true" = HTTPS via proxy, "false"/unset = HTTP direct
CANONICAL_URL=""
CANONICAL_HOST=""
if [ -n "$OST_HOST" ]; then
    CANONICAL_HOST="$OST_HOST"
    if [ "$OPENSPEEDTEST_HTTPS" = "true" ]; then
        if [ "$OST_HTTPS_PORT" = "443" ]; then
            CANONICAL_URL="https://$OST_HOST"
        else
            CANONICAL_URL="https://$OST_HOST:$OST_HTTPS_PORT"
        fi
    else
        CANONICAL_URL="http://$OST_HOST:$OST_PORT"
    fi
elif [ -n "$HOST_IP" ]; then
    CANONICAL_HOST="$HOST_IP"
    CANONICAL_URL="http://$HOST_IP:$OST_PORT"
fi

if [ -n "$CANONICAL_HOST" ] && [ -f "$NGINX_CONF" ]; then
    echo "Enforcing canonical URL: $CANONICAL_URL"

    # Redirect HTTP to HTTPS when behind a TLS proxy
    if [ "$OPENSPEEDTEST_HTTPS" = "true" ]; then
        sed -i "/server_name/a\\
    # Redirect HTTP to HTTPS\\
    if (\$http_x_forwarded_proto != \"https\") {\\
        return 302 $CANONICAL_URL\$request_uri;\\
    }" "$NGINX_CONF"
        echo "Added HTTP->HTTPS redirect rule"
    else
        # Host enforcement only (no scheme redirect)
        sed -i "/server_name/a\\
    # Enforce canonical host - prevents browser caching issues on mobile\\
    if (\$host != \"$CANONICAL_HOST\") {\\
        return 302 $CANONICAL_URL\$request_uri;\\
    }" "$NGINX_CONF"
        echo "Added host redirect rule"
    fi
fi

# Start nginx
exec "$@"
