#!/bin/sh
# Ozark Connect Speed Test - Entrypoint
# Injects runtime configuration into config.js

# API endpoint path (single source of truth)
API_PATH="/api/public/speedtest/results"

# Construct the save URL from environment variables
# Priority: REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
# IMPORTANT: Keep this logic in sync with NginxHostedService.cs:ConstructSaveDataUrl() (Windows installer)
if [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - use https and no port (proxy handles it)
    SAVE_DATA_URL="https://${REVERSE_PROXIED_HOST_NAME}${API_PATH}"
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

    # Replace placeholders with actual values
    sed -i "s|__SAVE_DATA__|$SAVE_DATA_VALUE|g" "$CONFIG_FILE"
    sed -i "s|__SAVE_DATA_URL__|$SAVE_DATA_URL|g" "$CONFIG_FILE"
    sed -i "s|__API_PATH__|$API_PATH|g" "$CONFIG_FILE"

    # Mobile HTTPS redirect URL (OPENSPEEDTEST_HTTPS=mobile)
    # When set, mobile browsers on HTTP are redirected to HTTPS for geolocation
    OST_HOST="${OPENSPEEDTEST_HOST:-$HOST_NAME}"
    OST_HTTPS_PORT="${OPENSPEEDTEST_HTTPS_PORT:-443}"
    if [ "$OPENSPEEDTEST_HTTPS" = "mobile" ] && [ -n "$OST_HOST" ]; then
        if [ "$OST_HTTPS_PORT" = "443" ]; then
            HTTPS_REDIRECT_URL="https://$OST_HOST"
        else
            HTTPS_REDIRECT_URL="https://$OST_HOST:$OST_HTTPS_PORT"
        fi
        sed -i "s|__HTTPS_REDIRECT_URL__|$HTTPS_REDIRECT_URL|g" "$CONFIG_FILE"
        echo "Mobile HTTPS redirect enabled: $HTTPS_REDIRECT_URL"
    else
        # Clear placeholder (no redirect)
        sed -i "s|__HTTPS_REDIRECT_URL__||g" "$CONFIG_FILE"
    fi

    echo "Configuration complete"
else
    echo "Warning: config.js not found at $CONFIG_FILE"
fi

# Enforce canonical URL via 302 redirect (matches UI logic exactly)
# Prevents browser caching issues on mobile
NGINX_CONF="/etc/nginx/conf.d/default.conf"
OST_PORT="${OPENSPEEDTEST_PORT:-3005}"
OST_HTTPS_PORT="${OPENSPEEDTEST_HTTPS_PORT:-443}"

# Match UI: OPENSPEEDTEST_HOST defaults to HOST_NAME
OST_HOST="${OPENSPEEDTEST_HOST:-$HOST_NAME}"

# Build canonical URL (same logic as ClientSpeedTest.razor)
# "mobile" uses proxy HTTP port (default 80), "false" uses direct nginx port (3005)
# "true" uses HTTPS proxy port (default 443)
OST_HTTP_PORT="${OPENSPEEDTEST_HTTP_PORT:-80}"
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
    elif [ "$OPENSPEEDTEST_HTTPS" = "mobile" ]; then
        if [ "$OST_HTTP_PORT" = "80" ]; then
            CANONICAL_URL="http://$OST_HOST"
        else
            CANONICAL_URL="http://$OST_HOST:$OST_HTTP_PORT"
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

    # Redirect HTTP to HTTPS when HTTPS is fully enabled (not "mobile" mode)
    # Check X-Forwarded-Proto (set by reverse proxy) - if not "https", we're on HTTP
    if [ "$OPENSPEEDTEST_HTTPS" = "true" ]; then
        sed -i "/server_name/a\\
    # Redirect HTTP to HTTPS\\
    if (\$http_x_forwarded_proto != \"https\") {\\
        return 302 $CANONICAL_URL\$request_uri;\\
    }" "$NGINX_CONF"
        echo "Added HTTP->HTTPS redirect rule"
    else
        # "mobile" and "false" both use host enforcement only (no scheme redirect)
        # Mobile HTTPS redirect is handled client-side by config.js
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
