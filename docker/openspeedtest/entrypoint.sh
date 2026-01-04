#!/bin/sh
# Patch OpenSpeedTest to send results to Network Optimizer API

# Construct the save URL if not explicitly set
# Priority: OPENSPEEDTEST_SAVE_URL > REVERSE_PROXIED_HOST_NAME > HOST_NAME > HOST_IP
if [ -n "$OPENSPEEDTEST_SAVE_URL" ]; then
    SAVE_DATA_URL="$OPENSPEEDTEST_SAVE_URL"
elif [ -n "$REVERSE_PROXIED_HOST_NAME" ]; then
    # Behind reverse proxy - use https and no port (proxy handles it)
    SAVE_DATA_URL="https://${REVERSE_PROXIED_HOST_NAME}/api/public/speedtest/results?"
elif [ -n "$HOST_NAME" ]; then
    SAVE_DATA_URL="http://${HOST_NAME}:8042/api/public/speedtest/results?"
elif [ -n "$HOST_IP" ]; then
    SAVE_DATA_URL="http://${HOST_IP}:8042/api/public/speedtest/results?"
else
    echo "Warning: No host configured for result reporting"
    echo "Set HOST_IP, HOST_NAME, or REVERSE_PROXIED_HOST_NAME in .env"
    SAVE_DATA_URL=""
fi

# Patch index.html where saveData and saveDataURL are defined
HTML_FILE="/usr/share/nginx/html/index.html"

if [ -f "$HTML_FILE" ] && [ -n "$SAVE_DATA_URL" ]; then
    echo "Patching OpenSpeedTest to send results to: $SAVE_DATA_URL"

    # Enable saveData (change false to true)
    sed -i 's/var saveData = false;/var saveData = true;/' "$HTML_FILE"

    # Set the save URL
    sed -i "s|var saveDataURL = \"[^\"]*\";|var saveDataURL = \"$SAVE_DATA_URL\";|" "$HTML_FILE"

    # Fix missing OpenSpeedTestdb variable (bug in OpenSpeedTest - referenced but never defined)
    # Add it right after saveDataURL definition
    if ! grep -q "var OpenSpeedTestdb" "$HTML_FILE"; then
        sed -i 's|var saveDataURL = |var OpenSpeedTestdb = ""; var saveDataURL = |' "$HTML_FILE"
        echo "Added missing OpenSpeedTestdb variable"
    fi

    # Verify the patch was applied
    if grep -q "saveData = true" "$HTML_FILE" && grep -q "$SAVE_DATA_URL" "$HTML_FILE"; then
        echo "OpenSpeedTest patched successfully"
    else
        echo "Warning: Patch may not have been applied correctly"
        grep "saveData" "$HTML_FILE"
    fi
elif [ ! -f "$HTML_FILE" ]; then
    echo "Warning: Could not find OpenSpeedTest index.html to patch"
elif [ -z "$SAVE_DATA_URL" ]; then
    echo "Warning: No save URL configured, results will not be reported"
fi

# Disable caching for HTML files (so patches take effect immediately)
NGINX_CONF="/etc/nginx/conf.d/OpenSpeedTest-Server.conf"
if [ -f "$NGINX_CONF" ]; then
    # Change "expires 365d" to "expires -1" (no cache)
    sed -i 's/expires 365d;/expires -1;/' "$NGINX_CONF"
    # Change Cache-Control public to no-cache
    sed -i 's/add_header Cache-Control public;/add_header Cache-Control "no-cache, no-store, must-revalidate";/' "$NGINX_CONF"
    echo "Disabled aggressive caching"

    # Enforce canonical host via 302 redirect (same priority as UI display: HOST_NAME > HOST_IP)
    CANONICAL_HOST=""
    if [ -n "$HOST_NAME" ]; then
        CANONICAL_HOST="$HOST_NAME"
    elif [ -n "$HOST_IP" ]; then
        CANONICAL_HOST="$HOST_IP"
    fi

    # External port (default 3005, the mapped port in docker-compose)
    CANONICAL_PORT="${OPENSPEEDTEST_PORT:-3005}"

    if [ -n "$CANONICAL_HOST" ]; then
        echo "Enforcing canonical host: $CANONICAL_HOST:$CANONICAL_PORT"
        # Add redirect rule inside the server block (after root directive)
        # This redirects any request where Host doesn't match the configured host
        sed -i "/root \/usr\/share\/nginx\/html/a\\
    # Enforce canonical host\\
    if (\$host != \"$CANONICAL_HOST\") {\\
        return 302 \$scheme://$CANONICAL_HOST:$CANONICAL_PORT\$request_uri;\\
    }" "$NGINX_CONF"
        echo "Added host redirect rule"
    fi
fi

# Run the original entrypoint (OpenSpeedTest's nginx setup)
exec /entrypoint.sh
