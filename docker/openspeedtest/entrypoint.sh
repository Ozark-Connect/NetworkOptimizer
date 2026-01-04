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
NGINX_CONF="/etc/nginx/conf.d/default.conf"
if [ -f "$NGINX_CONF" ]; then
    # Add no-cache for HTML files if not already present
    if ! grep -q "text/html.*no-cache" "$NGINX_CONF"; then
        sed -i '/location \/ {/a\        add_header Cache-Control "no-cache, no-store, must-revalidate";' "$NGINX_CONF"
        echo "Disabled caching for HTML files"
    fi
fi

# Run the original entrypoint (OpenSpeedTest's nginx setup)
exec /entrypoint.sh
